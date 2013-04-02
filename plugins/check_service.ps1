param([string]$service)
$_service = get-service -ErrorAction Ignore -Name $service
if ($_service.status -ne 'Running') {
    echo "CRITICAL - $service is not running"
    [Environment]::Exit(2)
} else {
    echo 'OK'
    exit 0
}