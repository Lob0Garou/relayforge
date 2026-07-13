[CmdletBinding()]
param(
    [string]$ApiBaseUrl = "http://localhost:5000",
    [string]$ReceiverUrl = "http://unstable-receiver:8080/webhooks/relayforge",
    [string]$ReceiverControlBaseUrl = "http://localhost:5001"
)

$ErrorActionPreference = "Stop"
$endpoint = Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/api/endpoints/" -ContentType "application/json" -Body (@{
    name = "local-demo"
    url = $ReceiverUrl
    timeoutSeconds = 5
} | ConvertTo-Json)

Write-Host "Endpoint created: $($endpoint.id)"
$secretBody = @{ secret = $endpoint.secret } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri "$ReceiverControlBaseUrl/control/secret" -ContentType "application/json" -Body $secretBody | Out-Null
$secretBody = $null
$endpoint.secret = $null
Invoke-RestMethod -Method Put -Uri "$ReceiverControlBaseUrl/operations/scenario" -ContentType "application/json" -Body (@{
    failuresBeforeSuccess = 2
    failureStatusCode = 503
    delayMilliseconds = 0
} | ConvertTo-Json) | Out-Null

$event = Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/api/events" -Headers @{ "Idempotency-Key" = "demo-$([guid]::NewGuid().ToString('N'))" } -ContentType "application/json" -Body (@{
    endpointId = $endpoint.id
    type = "demo.order.created"
    payload = @{ orderId = "demo-001"; amount = 42 }
} | ConvertTo-Json -Depth 4)

Write-Host "Accepted event $($event.eventId), delivery $($event.deliveryId)."
for ($attempt = 1; $attempt -le 20; $attempt++) {
    Start-Sleep -Milliseconds 500
    $delivery = Invoke-RestMethod -Uri "$ApiBaseUrl/api/deliveries/$($event.deliveryId)"
    $state = $delivery.delivery.status
    Write-Host "Delivery state: $state"
    if ($state -eq "Delivered") { Write-Host "Demo completed after $($delivery.delivery.attemptCount) attempts."; exit 0 }
}
throw "Delivery did not reach Delivered within the demo timeout."
