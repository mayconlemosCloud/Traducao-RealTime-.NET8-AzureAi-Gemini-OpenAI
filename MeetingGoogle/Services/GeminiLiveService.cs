using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MeetingGoogle.Models;
using NAudio.Wave;

namespace MeetingGoogle.Services
{
    public class GeminiLiveService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private ClientWebSocket? _webSocket;
        private string _apiKey = string.Empty;
        
        private BufferedWaveProvider? _waveProvider;
        private WaveOutEvent? _waveOut;
        private CancellationTokenSource? _cts;
        private bool _setupComplete;
        private bool _enableAudioOutput;

        private StringBuilder _currentModelResponse = new StringBuilder();
        private StringBuilder _currentUserInput = new StringBuilder();

        public event EventHandler<string>? PartialModelResponseReceived;
        public event EventHandler<string>? FinalModelResponseCompleted;
        public event EventHandler<string>? PartialUserInputReceived;
        public event EventHandler<string>? FinalUserInputCompleted;
        public event EventHandler<string>? LogMessageReceived;

        public bool IsPlayingAudio => _waveProvider != null && _waveProvider.BufferedBytes > 0;

        public GeminiLiveService()
        {
            _httpClient = new HttpClient();
        }

        public void Initialize(string apiKey)
        {
            _apiKey = apiKey?.Trim(' ', '"', '\'') ?? string.Empty;
        }

        public async Task<string> ValidateModelsAsync()
        {
            if (string.IsNullOrEmpty(_apiKey)) return "Falha: API Key ausente.";

            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={_apiKey}";
                var response = await _httpClient.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    return $"Erro na API: {response.StatusCode}\nDetalhes: {err}";
                }

                var content = await response.Content.ReadAsStringAsync();
                
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                if (root.TryGetProperty("models", out var models))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Modelos disponíveis (Live):");
                    foreach (var model in models.EnumerateArray())
                    {
                        if (model.TryGetProperty("name", out var nameProp))
                        {
                            var name = nameProp.GetString();
                            if (!string.IsNullOrEmpty(name) && (name.Contains("gemini-2") || name.Contains("live")))
                            {
                                sb.AppendLine($"- {name.Replace("models/", "")}");
                            }
                        }
                    }
                    return sb.ToString();
                }
                return "Nenhum modelo encontrado na resposta.";
            }
            catch (Exception ex)
            {
                return $"Erro de rede/validação: {ex.Message}";
            }
        }

        public async Task StartStreamingAsync(AudioDeviceInfo? outputDevice, bool enableAudioOutput)
        {
            if (string.IsNullOrEmpty(_apiKey)) throw new InvalidOperationException("API Key não configurada.");
            
            _cts = new CancellationTokenSource();
            _setupComplete = false;
            _webSocket = new ClientWebSocket();
            _currentModelResponse.Clear();
            _currentUserInput.Clear();
            
            var wsUrl = $"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={_apiKey}";
            
            Log("Conectando ao WebSocket...");
            
            try
            {
                await _webSocket.ConnectAsync(new Uri(wsUrl), _cts.Token);
            }
            catch (Exception ex)
            {
                Log($"ERRO ao conectar WebSocket: {ex.Message}");
                throw;
            }
            
            Log("WebSocket conectado!");

            _waveProvider = new BufferedWaveProvider(new WaveFormat(24000, 16, 1));
            _waveProvider.DiscardOnBufferOverflow = true;
            _waveProvider.BufferLength = 24000 * 2 * 15;
            _waveOut = new WaveOutEvent();
            if (outputDevice != null && outputDevice.DeviceIndex >= -1)
            {
                _waveOut.DeviceNumber = outputDevice.DeviceIndex;
            }
            _waveOut.Init(_waveProvider);
            _waveOut.Play();

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            
            await Task.Delay(200);
            
            _enableAudioOutput = enableAudioOutput;

            var generationConfig = new JsonObject
            {
                ["responseModalities"] = new JsonArray { "AUDIO" } // Gemini 2.5 Preview exige AUDIO
            };

            generationConfig["speechConfig"] = new JsonObject
            {
                ["voiceConfig"] = new JsonObject
                {
                    ["prebuiltVoiceConfig"] = new JsonObject
                    {
                        ["voiceName"] = "Aoede" // Voz padrão
                    }
                }
            };

                    var setupMsg = new JsonObject
                    {
                        ["setup"] = new JsonObject
                        {
                            ["model"] = "models/gemini-2.5-flash-native-audio-preview-12-2025",
                            ["generationConfig"] = generationConfig,
                            ["systemInstruction"] = new JsonObject
                            {
                                ["parts"] = new JsonArray
                                {
                                    new JsonObject { ["text"] = "Você é um tradutor simultâneo. Ouça o áudio e faça a tradução exata e literal para Português (Brasil) imediatamente. Nunca resuma o que foi dito, apenas traduza exatamente e concisamente o que ouvir. Fale a tradução de forma fluida." }
                                }
                            }
                        }
                    };

            var setupJson = setupMsg.ToJsonString();
            Log($"Enviando setup: {setupJson.Substring(0, Math.Min(300, setupJson.Length))}...");
            
            var setupBytes = Encoding.UTF8.GetBytes(setupJson);
            await _webSocket.SendAsync(new ArraySegment<byte>(setupBytes), WebSocketMessageType.Text, true, _cts.Token);
            Log("Setup enviado! Aguardando setupComplete...");

            var timeout = DateTime.UtcNow.AddSeconds(15);
            while (!_setupComplete && DateTime.UtcNow < timeout && !_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, _cts.Token);
            }
            
            if (!_setupComplete)
            {
                Log("⚠️ setupComplete NÃO recebido em 15s!");
            }
            else
            {
                Log("✅ Setup completo! Pronto para traduzir.");
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[1024 * 256];
            var messageStream = new MemoryStream();

            try
            {
                while (_webSocket?.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log("WebSocket fechado pelo servidor.");
                        try
                        {
                            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None);
                        }
                        catch { }
                        break;
                    }

                    messageStream.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        var jsonBytes = messageStream.ToArray();
                        var jsonMessage = Encoding.UTF8.GetString(jsonBytes);
                        messageStream.SetLength(0);
                        
                        ProcessServerMessage(jsonMessage);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"❌ Erro no Receive: {ex.Message}");
            }
        }

        private void ProcessServerMessage(string json)
        {
            try
            {
                var root = JsonNode.Parse(json);
                if (root == null) return;
                
                if (root["error"] != null)
                {
                    Log($"❌ ERRO DA API: {root["error"]?.ToJsonString()}");
                    return;
                }

                if (root["setupComplete"] != null)
                {
                    _setupComplete = true;
                    Log("🏁 setupComplete recebido!");
                    return;
                }

                var serverContent = root["serverContent"];
                if (serverContent != null)
                {
                    if (serverContent["interrupted"] != null)
                    {
                        Log("🗣️ Interrompido (barge-in)");
                        _waveProvider?.ClearBuffer();
                        FlushModelResponse();
                        FlushUserInput();
                        return;
                    }
                    
                    var modelTurn = serverContent["modelTurn"];
                    if (modelTurn != null)
                    {
                        // Quando o modelo começa a responder, o usuário terminou de falar e estamos processando
                        FlushUserInput();

                        var parts = modelTurn["parts"]?.AsArray();
                        if (parts != null)
                        {
                            foreach (var part in parts)
                            {
                                var text = part?["text"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    _currentModelResponse.Append(text);
                                    PartialModelResponseReceived?.Invoke(this, _currentModelResponse.ToString());
                                }

                                var inlineData = part?["inlineData"];
                                if (inlineData != null)
                                {
                                    var base64Data = inlineData["data"]?.GetValue<string>();
                                    if (!string.IsNullOrEmpty(base64Data) && _enableAudioOutput)
                                    {
                                        var audioBytes = Convert.FromBase64String(base64Data);
                                        _waveProvider?.AddSamples(audioBytes, 0, audioBytes.Length);
                                    }
                                }
                            }
                        }
                    }
                    
                    var inputTranscription = serverContent["inputTranscription"];
                    if (inputTranscription != null)
                    {
                        var inputText = inputTranscription["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(inputText))
                        {
                            _currentUserInput.Append(inputText);
                            PartialUserInputReceived?.Invoke(this, _currentUserInput.ToString());
                        }
                    }
                    
                    if (serverContent["turnComplete"] != null)
                    {
                        FlushModelResponse();
                        FlushUserInput(); 
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"[PARSE ERR] {ex.Message}");
            }
        }

        private void FlushModelResponse()
        {
            var text = _currentModelResponse.ToString().Trim();
            if (!string.IsNullOrEmpty(text))
            {
                FinalModelResponseCompleted?.Invoke(this, text);
            }
            _currentModelResponse.Clear();
        }

        private void FlushUserInput()
        {
            var text = _currentUserInput.ToString().Trim();
            if (!string.IsNullOrEmpty(text))
            {
                FinalUserInputCompleted?.Invoke(this, text);
            }
            _currentUserInput.Clear();
        }

        public async Task SendAudioAsync(byte[] pcmData, int count)
        {
            if (_webSocket?.State != WebSocketState.Open || !_setupComplete) return;

            var base64Audio = Convert.ToBase64String(pcmData, 0, count);
            var inputMsg = new JsonObject
            {
                ["realtimeInput"] = new JsonObject
                {
                    ["audio"] = new JsonObject
                    {
                        ["mimeType"] = "audio/pcm;rate=16000",
                        ["data"] = base64Audio
                    }
                }
            };

            var bytes = Encoding.UTF8.GetBytes(inputMsg.ToJsonString());
            try
            {
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
        }

        public async Task StopStreamingAsync()
        {
            _cts?.Cancel();
            _setupComplete = false;
            
            if (_webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client stopped", CancellationToken.None);
                }
                catch { }
            }
            
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _waveProvider = null;
        }

        private void Log(string message)
        {
            Debug.WriteLine($"[GeminiLive] {message}");
            LogMessageReceived?.Invoke(this, message);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _cts?.Cancel();
            _waveOut?.Dispose();
            _webSocket?.Dispose();
        }
    }
}
