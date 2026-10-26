using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CompanionOS.Core.Plugins;

/// <summary>
/// Current-user DPAPI storage for credentials explicitly assigned to external modules.
/// The file contains only authenticated DPAPI ciphertext; credential names are scoped to a
/// module id, and the same names are bound into DPAPI entropy to prevent ciphertext swapping.
/// </summary>
public sealed class ModuleSecretStore
{
    private const int MaximumSecrets = 128;
    private const int MaximumSecretCharacters = 16_384;
    private const long MaximumStoreBytes = 4 * 1024 * 1024;
    private static readonly object FileLock = new();
    private readonly string _filePath;

    public ModuleSecretStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public static ModuleSecretStore CreateDefault() =>
        new(CompanionOS.Core.Configuration.CompanionPaths.ModuleSecretsFile);

    public void Set(string moduleId, string secretName, string value)
    {
        ValidateIdentity(moduleId, secretName);
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumSecretCharacters ||
            value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException(
                $"Credential must contain 1 to {MaximumSecretCharacters:N0} characters and cannot contain line breaks or null characters.",
                nameof(value));
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(value);
        byte[] entropy = Entropy(moduleId, secretName);
        byte[] protectedValue;
        try
        {
            protectedValue = Dpapi.Protect(plaintext, entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
        }

        try
        {
            lock (FileLock)
            {
                Dictionary<string, string> entries = ReadEntries();
                string key = EntryKey(moduleId, secretName);
                if (!entries.ContainsKey(key) && entries.Count >= MaximumSecrets)
                {
                    throw new InvalidOperationException($"Iris can store at most {MaximumSecrets} module credentials.");
                }
                entries[key] = Convert.ToBase64String(protectedValue);
                WriteEntries(entries);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedValue);
        }
    }

    public string? Get(string moduleId, string secretName)
    {
        ValidateIdentity(moduleId, secretName);
        string? encoded;
        lock (FileLock)
        {
            encoded = ReadEntries().GetValueOrDefault(EntryKey(moduleId, secretName));
        }

        if (encoded is null)
        {
            return null;
        }

        byte[] protectedValue;
        try
        {
            protectedValue = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The module credential store contains an invalid entry.", exception);
        }

        byte[] entropy = Entropy(moduleId, secretName);
        byte[] plaintext;
        try
        {
            plaintext = Dpapi.Unprotect(protectedValue, entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedValue);
            CryptographicOperations.ZeroMemory(entropy);
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The module credential could not be decoded.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public bool Contains(string moduleId, string secretName)
    {
        ValidateIdentity(moduleId, secretName);
        lock (FileLock)
        {
            return ReadEntries().ContainsKey(EntryKey(moduleId, secretName));
        }
    }

    public bool Remove(string moduleId, string secretName)
    {
        ValidateIdentity(moduleId, secretName);
        lock (FileLock)
        {
            Dictionary<string, string> entries = ReadEntries();
            if (!entries.Remove(EntryKey(moduleId, secretName)))
            {
                return false;
            }
            WriteEntries(entries);
            return true;
        }
    }

    private Dictionary<string, string> ReadEntries()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        if (new FileInfo(_filePath).Length > MaximumStoreBytes)
        {
            throw new InvalidDataException("The module credential store exceeds its size limit and was left untouched.");
        }

        SecretFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SecretFile>(File.ReadAllText(_filePath));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The module credential store is damaged and was left untouched.", exception);
        }

        if (file is null || file.Schema != 1 || file.Entries is null || file.Entries.Count > MaximumSecrets)
        {
            throw new InvalidDataException("The module credential store has an unsupported or invalid format.");
        }
        return new Dictionary<string, string>(file.Entries, StringComparer.OrdinalIgnoreCase);
    }

    private void WriteEntries(Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string temporaryPath = _filePath + ".next";
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new SecretFile { Schema = 1, Entries = entries });
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string EntryKey(string moduleId, string secretName) =>
        $"{moduleId.ToLowerInvariant()}:{secretName.ToLowerInvariant()}";

    private static byte[] Entropy(string moduleId, string secretName) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"ProjectIRIS.module-secret.v1\0{EntryKey(moduleId, secretName)}"));

    private static void ValidateIdentity(string moduleId, string secretName)
    {
        if (!IsIdentifier(moduleId, allowDot: true) || !IsIdentifier(secretName, allowDot: true))
        {
            throw new ArgumentException("Module ids and credential names must be ASCII identifiers.");
        }
    }

    private static bool IsIdentifier(string value, bool allowDot) =>
        value.Length is > 0 and <= 64 && char.IsAsciiLetter(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ||
                               (allowDot && character == '.'));

    private sealed class SecretFile
    {
        public int Schema { get; init; }
        public Dictionary<string, string> Entries { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static class Dpapi
    {
        private const int CryptProtectUiForbidden = 0x1;

        public static byte[] Protect(byte[] plaintext, byte[] entropy) => Transform(plaintext, entropy, protect: true);

        public static byte[] Unprotect(byte[] ciphertext, byte[] entropy) => Transform(ciphertext, entropy, protect: false);

        private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Module credentials use Windows DPAPI and require Windows.");
            }

            DataBlob inputBlob = Allocate(input);
            DataBlob entropyBlob = Allocate(entropy);
            DataBlob outputBlob = default;
            try
            {
                bool succeeded = protect
                    ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out outputBlob)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out outputBlob);
                if (!succeeded)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect or unlock this module credential.");
                }

                byte[] result = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                ClearAndFree(inputBlob);
                ClearAndFree(entropyBlob);
                if (outputBlob.Data != IntPtr.Zero)
                {
                    ZeroMemory(outputBlob.Data, (nuint)Math.Max(0, outputBlob.Length));
                    _ = LocalFree(outputBlob.Data);
                }
            }
        }

        private static DataBlob Allocate(byte[] bytes)
        {
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return new DataBlob { Length = bytes.Length, Data = pointer };
        }

        private static void ClearAndFree(DataBlob blob)
        {
            if (blob.Data != IntPtr.Zero)
            {
                ZeroMemory(blob.Data, (nuint)Math.Max(0, blob.Length));
                Marshal.FreeHGlobal(blob.Data);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description,
            ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
            ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
        private static extern void ZeroMemory(IntPtr destination, nuint length);
    }
}
