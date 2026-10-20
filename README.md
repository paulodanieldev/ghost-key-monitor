# Ghost Key Monitor (Windows 11, 100% nativo)

Detecta "toque fantasma" (tecla enviando sinais sem ser pressionada, ou tecla que fica presa) em
segundo plano e deixa você escolher: tentar resolver pressionando a tecla, ou desativá-la até reiniciar.

Sem Python e sem dependências: é C# compilado com o `csc.exe` que já vem no Windows.

## Compilar

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Roda os testes do detector e gera `GhostKeyMonitor.exe` (único arquivo necessário).

## Instalar / desinstalar

O próprio `.exe` é o instalador (na Área de Trabalho: `Instalar Ghost Key Monitor.exe`). Ao abri-lo
aparece o assistente: instala para o seu usuário (sem administrador) em
`%LOCALAPPDATA%\Programs\GhostKeyMonitor`, cria a pasta **Ghost Key Monitor** no Menu Iniciar (com o
programa e "Desinstalar Ghost Key Monitor"), atalho opcional na Área de Trabalho e início com o Windows.
Abrir o instalador de novo, com o programa já instalado, oferece reinstalar/atualizar ou desinstalar.

- Desinstalar (remove tudo: programa, configurações, log, atalhos, início automático, registro e a
  memória do ícone da bandeja): **Configurações > Aplicativos > Ghost Key Monitor**, ou Menu Iniciar >
  **Desinstalar Ghost Key Monitor**, ou botão **Desinstalar...** na janela / menu da bandeja.
  O arquivo instalador da Área de Trabalho é seu e não é apagado.
- Sem interface: `--install --silent [--desktop] [--autostart] [--launch]` e `--uninstall --silent`.

## Usar

- Um ícone aparece na bandeja. No Windows 11 ele costuma ficar nos **ícones ocultos** (seta ^ perto do
  relógio): clique nele (ou abra o programa pelo Menu Iniciar) para abrir a janela.
  Fechar a janela só a esconde; para encerrar use **Sair**.
- **Configurações...**: sensibilidade de cada padrão, notificação do Windows e **Iniciar com o Windows**.
  Ficam salvas em `settings.ini`, ao lado do `.exe`.
- Log: `ghost_key_log.txt` ao lado do `.exe`. Só eventos de teclas classificadas como fantasma são
  registrados, nunca o que você digita.

## Padrões detectados

| Padrão | O que é | Severidade |
|---|---|---|
| STUCK_KEY | tecla pressionada há mais de 5 s sem KEY_UP (20 s para Shift/Ctrl/Alt/Win) | CRÍTICO |
| DOUBLE_DOWN | 2 KEY_DOWN sem KEY_UP em < 200 ms (o auto-repeat do Windows é ignorado) | CRÍTICO |
| RAPID_REPEAT | 5+ toques rápidos da mesma tecla em 2 s (UP→DOWN < 50 ms, ou DOWN→UP < 20 ms). Um toque rápido isolado, como uma dupla batida, não acusa | ALTO |
| ORPHAN_UP | KEY_UP sem KEY_DOWN (ignorado nos 3 s iniciais) | ALTO |
| BURST | 5+ pressionamentos da mesma tecla em 500 ms | MÉDIO |

O **5** é a "Recorrência mínima" (Configurações...; padrão 5, de 2 a 20) e vale para RAPID_REPEAT e BURST.

Em STUCK_KEY, "Pressionar para tentar resolver" também envia um KEY_UP por software para o Windows
soltar a tecla. Ajuste a sensibilidade nos campos públicos no topo de `GhostKeyDetector.cs` e recompile.

## Opções de linha de comando

`--background` (só bandeja) · `--accept-injected`, `--demo`, `--stuck-ms=1500` (para teste)
