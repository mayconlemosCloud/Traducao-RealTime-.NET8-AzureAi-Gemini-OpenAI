# Plano de Refatoração: MeetingGoogle para Gemini Multimodal Live

Este documento descreve as etapas para converter o projeto WPF .NET 8 **MeetingGoogle** para uma arquitetura de "Serviço Único", utilizando a API **Gemini 1.5 Multimodal Live via WebSockets**.

## Contexto e Objetivo
O objetivo principal é eliminar a redundância de serviços legados (STT isolado, Tradução isolada, TTS isolado) e substituí-los por uma única conexão bidirecional com o Gemini Live, que fará todo o processo em tempo real.

---

## 🚀 Etapas da Refatoração

### 0. Validação de Credenciais / Modelos Disponíveis
- [ ] Ler a `GEMINI_API_KEY` do arquivo `.env`.
- [ ] Fazer uma chamada REST à API do Gemini (`https://generativelanguage.googleapis.com/v1beta/models`) para listar os modelos habilitados e confirmar acesso.

### 1. Limpeza do Legado
- [ ] Deletar `SttService.cs`.
- [ ] Deletar `TtsService.cs`.
- [ ] Deletar `TranslationService.cs`.
- [ ] Remover pacotes NuGet redundantes: `Google.Cloud.Speech.V1`, `Google.Cloud.TextToSpeech.V1`.
- [ ] Remover lógicas de encadeamento no `MainViewModel.cs`.

### 2. Implementação da Nova Arquitetura
- [ ] Criar novo serviço: `GeminiLiveService.cs` na pasta de serviços.
- [ ] Implementar conexão WebSocket para o endpoint `v1alpha/GenerativeService.BidiGenerateContent`.
- [ ] Configuração do Modelo:
  - **Modelo:** `gemini-1.5-flash`.
  - **Instrução:** "Você é um tradutor simultâneo. Ouça o áudio, transcreva o original e forneça a tradução para Português imediatamente. Use o recurso de Native Audio Output para falar a tradução com voz natural."
  - **Voice Configuration:** Escolher uma voz HD natural (ex. `Aoede`).

### 3. Integração de Áudio com NAudio
- [ ] **Input:** Capturar áudio 16-bit PCM via `AudioCaptureService` e enviar em chunks para o WebSocket.
- [ ] **Output:** Receber chunks de áudio do Gemini e reproduzir usando `BufferedWaveProvider` do `NAudio` (sem cliques ou interrupções).

### 4. Atualização da Interface e ViewModel
- [ ] Em `MainViewModel.cs`, substituir instâncias antigas por `GeminiLiveService`.
- [ ] Conectar evento de `TranscriptionReceived` do Gemini direto à propriedade `SubtitleText` da UI.
- [ ] Garantir que o comando de "Conectar" inicie a captura de áudio e a sessão WebSocket simultaneamente.

### 5. Requisitos de Ambiente
- [ ] Garantir a leitura da `GEMINI_API_KEY` do arquivo `.env` para autenticação no WebSocket.

---
**Status Atual:** Aguardando revisão inicial do plano.
