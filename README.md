# Spotify Taskbar Widget

Mini-widget do Spotify embutido na barra de tarefas do Windows 11: mostra a capa,
o título e o artista da música atual, com botões de anterior / play-pausa / seguinte.

![preview](docs/preview.png)

## Como funciona

- Os dados vêm da **API de media do Windows (SMTC)** — a mesma que alimenta o popup
  de volume do Windows. O Spotify desktop publica lá a música atual, por isso
  **não é preciso login nem chave da API do Spotify**.
- A barra do Windows 11 já não suporta "deskbands", por isso o widget é uma janela
  sem bordas, sempre visível, encaixada por cima da zona vazia da barra.
- **Posição automática:** via UI Automation, o widget encontra o botão de
  widgets/tempo (`WidgetsButton`) e alinha-se logo a seguir; se os widgets do
  Windows estiverem desativados, alinha à borda esquerda da barra. Nunca invade
  os ícones centrados (`StartButton`) — se o espaço for pouco (ecrãs pequenos),
  a coluna de texto encolhe. Funciona em qualquer resolução/DPI.
- Esconde-se automaticamente quando uma app está em ecrã inteiro (jogos, vídeos).

## Utilização

- **Executável:** `publish\SpotifyTaskbarWidget.exe`
- **Posição:** bloqueada e automática por defeito (a seguir ao widget do tempo).
  Para mudar: botão direito → *Mover widget*, arrasta, e desmarca para bloquear
  na nova posição. *Repor posição automática* volta ao alinhamento automático.
- **Tamanho:** botão direito → *Tamanho* → Pequeno / Normal / Grande.
- **Botões:** botão direito → *Botões* para escolher quais aparecem —
  favoritos (+), aleatório, anterior, seguinte, volume. Em ecrãs pequenos os
  menos importantes escondem-se sozinhos (volume → aleatório → favoritos → …).
- **Favoritos (+):** lê e controla o botão do próprio Spotify através da árvore
  de acessibilidade da janela dele (`SpotifyUiaService`) — mostra o **visto
  verde** quando a música já está nos favoritos e o clique adiciona sem roubar
  o foco. Sem janela do Spotify disponível, recorre ao atalho Alt+Shift+B.
- **Aleatório:** os 3 modos do Spotify — desligado (branco), aleatório (verde)
  e **aleatório inteligente** (verde com estrela). O clique cicla os modos como
  no Spotify. O estado vem da acessibilidade, com o SMTC como rede de segurança
  (nesse caso só liga/desliga).
- **Volume:** move o slider de volume do próprio Spotify (a UI dele acompanha);
  sem janela disponível, ajusta o volume da app no mixer do Windows.
- **Clique** na capa/texto: abre a janela do Spotify.
- Definições e log de erros em `%APPDATA%\SpotifyTaskbarWidget\`.

## Visual

Réplica do tema do Spotify: ícones vetoriais oficiais (paths SVG do leitor web),
verde #1ED760 nos estados ativos, cinzento #B3B3B3 nos inativos, ponto verde sob
o aleatório ativo, estrela no modo inteligente, hover que amplia os ícones e
slider de volume horizontal com preenchimento verde e bolinha branca.

## Atualizações

- Menu → **Procurar atualizações** (ao arrancar também verifica em silêncio e
  realça o item do menu se houver versão nova). A atualização descarrega o novo
  exe do GitHub Releases, substitui-se e reinicia.
- Para publicar uma atualização:
  1. definir o repositório em `UpdateService.cs` (`GitHubRepo`, substituir o `CHANGEME`);
  2. subir `<Version>` no `.csproj`;
  3. `dotnet publish` e criar uma release no GitHub com tag `vX.Y.Z`,
     anexando o `publish\SpotifyTaskbarWidget.exe`.
- Se uma atualização do Spotify partir a integração por acessibilidade
  (tick/modo inteligente/volume interno), o widget degrada para o SMTC
  (play/pausa/faixa/capa continuam a funcionar) e podes corrigir as
  heurísticas em `SpotifyUiaService.cs` e lançar uma release nova.

## Compilar

Requer o .NET 8 SDK:

```
dotnet publish SpotifyTaskbarWidget.csproj -c Release -o publish
```

Sai um único `SpotifyTaskbarWidget.exe` (precisa do .NET 8 Desktop Runtime;
para um exe autónomo sem esse requisito, mudar `SelfContained` para `true`).

## Estrutura

| Ficheiro | Função |
|---|---|
| `MainWindow.xaml(.cs)` | UI do widget, posicionamento, layout responsivo, menu |
| `MediaService.cs` | API de media do Windows (faixa, capa, play/pausa, aleatório) |
| `TaskbarAnchors.cs` | UI Automation: âncoras do botão de widgets e do botão Iniciar |
| `SpotifyUiaService.cs` | Acessibilidade da janela do Spotify: favoritos, 3 modos aleatórios, volume |
| `SpotifyVolume.cs` | CoreAudio (recurso): volume da app no mixer do Windows |
| `SpotifyActions.cs` | Recurso: favoritos via Alt+Shift+B; abrir a janela do Spotify |
| `Interop.cs` | Win32 (posição da barra, topmost, ecrã inteiro, teclado) |
| `WidgetSettings.cs` | Posição, escala e botões visíveis, em JSON |
