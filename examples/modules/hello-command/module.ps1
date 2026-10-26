$ErrorActionPreference = 'Stop'

function Write-JsonLine($Value) {
    [Console]::Out.WriteLine(($Value | ConvertTo-Json -Compress -Depth 20))
}

$requestLine = [Console]::In.ReadLine()
if ($null -eq $requestLine) {
    [Console]::Error.WriteLine('Iris did not send a request line.')
    exit 2
}

$request = ConvertFrom-Json -InputObject $requestLine
if ($request.action -eq 'tools/list') {
    Write-JsonLine @{
        type = 'tools'
        tools = @(
            @{
                name = 'say_hello'
                description = 'Greet someone by name.'
                schema = @{
                    type = 'object'
                    properties = @{
                        name = @{ type = 'string'; description = 'The name to greet.' }
                    }
                    required = @('name')
                    additionalProperties = $false
                }
            }
        )
    }
    exit 0
}

if ($request.action -ne 'call' -or $request.tool -ne 'say_hello') {
    Write-JsonLine @{ type = 'result'; ok = $false; text = 'Unsupported action or tool.' }
    exit 0
}

$name = [string]$request.arguments.name
if ([string]::IsNullOrWhiteSpace($name)) {
    Write-JsonLine @{ type = 'result'; ok = $false; text = 'Please enter a name first.' }
    exit 0
}

$name = $name.Trim()
Write-JsonLine @{ type = 'progress'; message = 'Preparing a greeting' }
Write-JsonLine @{ type = 'ui'; surface = 'greeting'; element = 'reply'; value = "Writing a greeting for $name..." }
Write-JsonLine @{ type = 'result'; ok = $true; text = "Hello, $name!" }
