param(
    [string]$DeviceHost = '127.0.0.1',
    [int]$Port = 8500,
    [ValidateSet('STATUS', 'TRIGGER')]
    [string]$Command = 'STATUS',
    [string]$RequestId = [Guid]::NewGuid().ToString('N')
)

$ErrorActionPreference = 'Stop'
$client = [System.Net.Sockets.TcpClient]::new()
try {
    $connection = $client.ConnectAsync($DeviceHost, $Port)
    if (-not $connection.Wait(3000)) { throw 'TCP 连接超时' }
    [void]$connection.GetAwaiter().GetResult()
    $stream = $client.GetStream()
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false), 1024, $true)
    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $false, 1024, $true)
    try {
        $writer.AutoFlush = $true
        $writer.WriteLine((@{ command = $Command; requestId = $RequestId } | ConvertTo-Json -Compress))
        $response = $reader.ReadLineAsync()
        if (-not $response.Wait(10000)) { throw '等待检测响应超时；可用同一 RequestId 重试查询原结果' }
        $response.GetAwaiter().GetResult()
    }
    finally { $writer.Dispose(); $reader.Dispose() }
}
finally { $client.Dispose() }
