$apiKey = "AIzaSyANovo-LP6H0qDysY4ohPWB3DRM2kEt-y8"
$response = Invoke-RestMethod -Uri "https://generativelanguage.googleapis.com/v1beta/models?key=$apiKey"
$response.models | Select-Object name, displayName | Format-Table -AutoSize
