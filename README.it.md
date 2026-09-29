# BrowserForWP

**Un browser con trasporto moderno per Windows Phone 8.1 — crittografia, TLS e
DNS girano sul telefono, mentre le pagine che questo telefono non sa disegnare
le disegna un server ospitato. Quel server è il motore predefinito, è a un
interruttore di distanza dall'essere spento, e questa pagina dice quanto costa
prima che tu lo usi.**

[English](README.md) · [Italiano](README.it.md)

---

## Da leggere subito: cosa è realmente possibile su Windows Phone 8.1

Questo progetto fa una promessa insolitamente onesta, quindi ecco la verità
tecnica prima di qualsiasi elenco di funzionalità.

Windows Phone 8.1 è una **piattaforma chiusa, pubblicata nel 2014 e
abbandonata a luglio 2017**. Il suo stack di navigazione è **Trident (Internet
Explorer 11)** e il sistema operativo **non offre alle app di terze parti alcun
modo di sostituire il motore di rendering**. È un limite strutturale del
sistema, non un limite delle ambizioni di questo progetto.

| Obiettivo | Realtà su Windows Phone 8.1 | Cosa fa BrowserForWP |
| --- | --- | --- |
| Includere il motore **Chromium** | Non esiste alcuna build di Chromium/Blink per WinRT-ARM 8.1. I container delle app non possono ospitare un renderer multi-processo in sandbox. | Fornisce un `IBrowserEngine` sostituibile. Su WP8.1 distribuisce `TridentEngine`; `WebView2Engine` (Chromium) e `GeckoViewEngine` (Firefox) si innestano su qualunque piattaforma li possieda. Dal Round 10 esiste una terza possibilità che non richiede alcun port: il **motore ospitato** esegue Chromium su un server e manda l'immagine attraverso il canale TLS 1.3 dell'app. È il motore con cui questa build esce impostata, e dalle Impostazioni lo sostituisci con qualunque server tuo. Vedi *Renderer ospitato per impostazione predefinita* qui sotto e la Legge 5 in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). |
| Includere il motore **Firefox / Gecko** | Mozilla ha cancellato Firefox per Windows Phone nel 2015. Nessun binario è mai stato distribuito. | Stessa astrazione sostituibile di cui sopra. |
| **TLS 1.3** | Schannel su WP8.1 si ferma a **TLS 1.2** e il sistema non espone alcuna API per alzare il limite. | **Implementato dalle RFC, in codice gestito, sul dispositivo**: un client TLS 1.3 completo (`BrowserForWP.Net`) che gira su un `StreamSocket` grezzo, così il livello di rete dell'app parla TLS 1.3 già oggi. |
| **HTTPS moderno** | La `WebView` di sistema negozia ciò che Schannel supporta. | `Tls13Client` + resolver DNS-over-HTTPS + pinning dei certificati per il livello di trasporto dell'app. Il pin non copre però il canale di rendering del motore remoto: `Tls13Client` accetta un host e nessuna tabella di pin. La lacuna è registrata in `docs/MAINTAINING.md`, non lasciata da scoprire. |
| **Pagine web moderne** | IE11 non riesce a interpretare né a eseguire il JavaScript moderno. | Un bundle di compatibilità ES5 sul dispositivo (`BrowserForWP.Polyfill`) iniettato a `DOMContentLoaded` e di nuovo al completamento, più una diagnostica che spiega *perché* un sito ha fallito. Il bundle alza il livello minimo ma non può interpretare la sintassi ES6 né fornire `Proxy`/`Intl`/grid — vedi l'elenco qui sotto. |
| **Un motore da zero** | Su questo sistema non si può costruire un motore *al posto di* Trident, e Trident non è riconfigurabile (vedi [`docs/MAINTAINING.md`](docs/MAINTAINING.md), sezione *IE-adaptation is closed*). | Una **pipeline di documenti** vive in `BrowserForWP.Core/Engine/Native`: recupera una pagina attraverso il trasporto TLS 1.3 dell'app — l'unico percorso di questo prodotto che può caricare qualcosa sopra TLS 1.2 — e analizza un **sottoinsieme dichiarato** di HTML e CSS producendo un albero di box, visibile in **Diagnostica → Analizza la pagina corrente**. **Non esegue JavaScript** e mai lo farà. Un *renderer* sul dispositivo per quell'albero è stato costruito nel Round 7 e **cancellato nel Round 10**: era una cosa più piccola di un browser, e mantenere due renderer per dimostrarlo era lo scambio sbagliato (Legge 5). |
| **Renderer ospitato per impostazione predefinita** | — | Ogni componente — crittografia, TLS, DNS, polyfill, cronologia, localizzazione — gira sul telefono. L'eccezione è il *rendering*, ed è l'eccezione predefinita: le pagine vanno a un server che le disegna con Chromium, e **chi gestisce quel server può leggere tutto quello che leggi tu, password comprese**. L'indirizzo esce già impostato sul server ospitato di questo progetto. Puoi puntarlo a un server tuo, oppure disattivarlo e tenere ogni pagina sul telefono: la Legge 5 in `docs/ARCHITECTURE.md` è il costo completo, e le Impostazioni lo dicono accanto all'interruttore. Un dispositivo non ancora registrato, o un server che non risponde, ripiegano sul motore sul dispositivo e lo scrivono nella barra di stato. |

> **Sull'idea del proxy locale sul dispositivo:** i Windows AppContainer
> bloccano per impostazione predefinita il traffico verso `127.0.0.1`, quindi un
> proxy locale non può alimentare la `WebView` di sistema. Questa architettura è
> volutamente **non** utilizzata. Vedi
> [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) per l'analisi completa dei
> vincoli.

**In sintesi:** ottieni un livello di *trasporto* realmente moderno (TLS 1.3,
DoH, pinning) su un livello di *rendering* invariato — perché su questo sistema
il rendering non è modificabile. Il livello di *contenuto* (polyfill) viene
iniettato in ogni pagina a `DOMContentLoaded` e di nuovo al completamento;
alza il livello minimo per i siti che rilevano le funzionalità, ma non può
correggere la sintassi ES6 né le funzionalità motore mancanti. L'astrazione del motore significa che il giorno in cui punterai
questo codice a un dispositivo con un motore moderno vero, i livelli di
trasporto e contenuto verranno con te.

---

## Funzionalità

- **Interfaccia browser completa** — barra degli indirizzi, indietro / avanti /
  ricarica / stop, schede, indicatore di avanzamento, stato di sicurezza per
  sito.
- **Barra degli indirizzi intelligente** — normalizza l'input, distingue URL da
  query di ricerca, ripristina gli schemi mancanti e rifiuta gli schemi
  pericolosi.
- **Trasporto TLS 1.3 (RFC 8446)** — implementazione gestita su
  `Windows.Networking.Sockets.StreamSocket`.
- **DNS over HTTPS (RFC 8484)** — risolve i nomi host fuori dal canale
  tradizionale, così un resolver locale obsoleto o dirottato non può rompere o
  redirigere la navigazione.
- **Pinning dei certificati** — pin per sito gestiti dall'utente, con override
  esplicito e reversibile (rimuovere il pin lo annulla). Il pin viene confrontato
  con lo SPKI della foglia quando si connette il trasporto TLS 1.3 dell'app. Il
  traffico della `WebView` passa da Schannel, la cui validazione l'app non può
  intercettare. Un pin protegge quindi il livello di trasporto dell'app — che
  comprende le pagine recuperate dal parser di diagnostica — ma non le pagine che
  *visualizzi* nella `WebView`, e nemmeno il canale di rendering del **motore
  remoto**: `Tls13Client` accetta un host e nessuna tabella di pin. La lacuna è
  registrata in [`docs/MAINTAINING.md`](docs/MAINTAINING.md) invece di essere
  lasciata da scoprire.
- **Bundle di compatibilità ES5** (`BrowserForWP.Polyfill/compat.js`) — scritto,
  verificato ES5, incluso e iniettato a `DOMContentLoaded` e al completamento
  tramite `TridentEngine.InjectPolyfillAsync`. Alza il livello minimo; non può
  interpretare la sintassi ES6 né fornire `Proxy`/`Intl`/grid. Vedi
  l'elenco qui sotto.
- **Interfaccia bilingue** — inglese e italiano, scelti automaticamente dalla
  lingua di visualizzazione del telefono, con override per singola app.
- **Diagnostica** — una sonda integrata che segnala esattamente quale
  funzionalità moderna ha causato il fallimento di una pagina, così il limite è
  visibile invece che misterioso.

## Struttura del repository

```
BrowserForWP/
├── BrowserForWP.sln            Soluzione Visual Studio 2013+
├── BrowserForWP/              App Windows Phone 8.1 (VB.NET / WinRT / XAML)
│   ├── MainPage.xaml(.vb)     Interfaccia del browser
│   ├── Assets/                Logo, tile, splash (generati)
│   └── Strings/               Risorse UI en-US / it-IT
├── BrowserForWP.Core/         Astrazione del motore, schede, cronologia
├── BrowserForWP.Net/          TLS 1.3, DoH, client HTTP
├── BrowserForWP.Crypto/       HKDF, X25519, ChaCha20-Poly1305, AES-GCM
├── BrowserForWP.Localization/ Risoluzione della lingua + lookup delle stringhe
├── BrowserForWP.Polyfill/     Bundle JS di compatibilità sul dispositivo
├── docs/
│   ├── ARCHITECTURE.md        Progettazione + analisi dei vincoli di piattaforma
│   ├── MAINTAINING.md         Come compilare, eseguire ed estendere il progetto
│   └── superpowers/plans/     Piani di implementazione (uno per funzionalità)
├── tests/                     Test unitari (eseguiti in Visual Studio)
├── tools/
│   ├── make_logo.py           Rigenera tutte le immagini partendo dall'SVG
│   └── gen-vectors.mjs        Genera i vettori noti delle RFC per i test
└── .agents/skills/browserforwp/SKILL.md
                               Skill del progetto: piano → commit → push → estensione
```

## Compilazione

Requisiti: **Visual Studio 2013 Update 4 o successivo** con *Windows Phone 8.1
SDK*, su Windows. La soluzione ha come target `TargetPlatformVersion 8.1` e
`WindowsPhoneApp`.

```
1. Apri BrowserForWP.sln
2. Seleziona un target telefono: Debug | ARM  (dispositivo) oppure Debug | x86 (emulatore)
3. Distribuisci su un telefono sbloccato per sviluppatori o sull'emulatore WP8.1
```

> Il motore di rendering, la crittografia e il codice TLS non possono essere
> eseguiti su macOS o Linux: l'SDK WP8.1 è solo per Windows. La crittografia
> puramente gestita in `BrowserForWP.Crypto` è rispecchiata da `tests/`, che
> viene eseguito in Visual Studio.

Rigenera immagini e vettori di test in qualsiasi momento:

```bash
python3 tools/make_logo.py          # riscrive BrowserForWP/Assets/*.png
node tools/gen-vectors.mjs          # riscrive tools/out/*.json
```

## Localizzazione

L'app include **en-US** (predefinita) e **it-IT**. La lingua di visualizzazione
viene scelta in quest'ordine:

1. Un override per singola app, se l'utente ne ha impostato uno.
2. `Windows.Globalization.ApplicationLanguages.Languages` — l'elenco ordinato
   delle lingue del telefono.
3. Fallback: `en-US`.

Aggiungere una lingua significa aggiungere una cartella di risorse e una voce
nella tabella delle lingue — nessuna modifica al codice. Vedi
[`docs/MAINTAINING.md`](docs/MAINTAINING.md#aggiungere-una-lingua).

## Contribuire

Leggi [`docs/MAINTAINING.md`](docs/MAINTAINING.md) per il flusso di
compilazione/test e [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) prima di
modificare i livelli del motore o del trasporto. La skill del progetto in
[`.agents/skills/browserforwp/SKILL.md`](.agents/skills/browserforwp/SKILL.md)
descrive il ciclo obbligatorio piano → test → commit → push.

## Licenza

[MIT](LICENSE) © 2026 vincenzosco

---

## Dichiarazione: questo progetto è fatto al 100% da IA

Ogni parte di BrowserForWP — l'architettura, lo stack TLS 1.3, la crittografia,
l'interfaccia, gli strumenti, la documentazione e questa nota — è stata scritta
da un agente di programmazione IA, con una persona che ha diretto il lavoro e
rivisto il risultato passo per passo.

È una affermazione concreta su quanto fidarsi, quindi ecco la posizione onesta
invece che un vanto:

- **Compila.** L'intera soluzione si compila con MSBuild 12 / Visual Studio 2013
  per `Debug|ARM` e `Release|ARM` dentro una VM Windows 11 ARM64, e produce un
  pacchetto installabile. Ci sono voluti quattro giri; il resoconto di ogni
  famiglia di errori — e delle ipotesi rivelatesi sbagliate — è in
  [`docs/MAINTAINING.md`](docs/MAINTAINING.md).
- **Fuori da Windows, `tools/check-vb.mjs` è un filtro, non un verdetto.** Esegue
  dodici categorie di controlli meccanici su qualunque macchina e ha trovato
  difetti reali, ma non fa type-check. Tutto ciò che non può vedere è fallito
  nella VM pur passando qui: un membro `Friend` usato da un altro assembly, una
  classe annidata nominata senza qualifica da un terzo file, una variabile
  locale che oscura un tipo, e una chiave XAML `{ThemeResource}` che WP8.1 non
  definisce.
- **La crittografia e il protocollo TLS 1.3 sono verificati, ma non su un
  telefono.** Passano `tools/gen-vectors.mjs` (52 asserzioni contro RFC
  5869/7748/8439/8448 e NIST AES-GCM), `tools/proto/w25519.mjs` (18 controlli) e
  `tools/proto/tls13.mjs` (31 controlli, con handshake reali verso Google,
  Cloudflare ed example.com).
- **Esiste un secondo motore, ma solo la sua prima metà.**
  `BrowserForWP.Core/Engine/Native` recupera una pagina attraverso il trasporto
  TLS 1.3 dell'app e analizza un sottoinsieme dichiarato di HTML e CSS
  producendo un albero di box. **Non esegue JavaScript**, e mai lo farà. Non
  esistono ancora né layout né disegno: l'output della pipeline è testo, stampato
  in **Diagnostica → Analizza la pagina corrente**. Il suo comportamento è
  verificato dai prototipi che lo rispecchiano (`tools/proto/htmlparse.mjs`,
  `csscascade.mjs`, `boxtree.mjs`) più la build nella VM; come tutto il resto,
  non è mai stato eseguito su un telefono.
- **Non è mai stato eseguito su un telefono.** Layout XAML, comportamento del
  WebView e prestazioni su hardware del 2014 non sono verificati. Compilare non
  significa eseguire.
- **Il bundle di compatibilità ha limiti architetturali.** Viene iniettato a
  `DOMContentLoaded` e di nuovo al completamento, e alza il livello minimo per
  i siti che rilevano le funzionalità — ma nessuno script iniettato può
  interpretare la sintassi ES6 che il motore rifiuta, né fornire `Proxy`,
  `Intl` o la grid CSS. La sonda di compatibilità riporta esattamente quale
  limite una pagina ha colpito, e una modalità lettura di ripiego più i
  redirect alle versioni leggere coprono il resto.
- **Una affermazione precedente di questo README era sbagliata, e qui c'è la
  correzione.** Una revisione precedente sosteneva che l'app non potesse essere
  compilata, perché l'unica VM Windows disponibile è ARM64 e Microsoft non
  supporta Visual Studio precedente alla 17.4 su processori Arm. Quella
  documentazione riguarda l'**IDE**; non dice nulla sulla **compilazione da riga
  di comando**, che funziona. Nessuno l'aveva provata. È stata provata, e
  compila.
- **Le affermazioni sono state verificate e quelle false eliminate.** Chromium e
  Firefox non possono girare su questo sistema operativo e TLS 1.3 non è
  ottenibile da esso; entrambi i fatti sono dichiarati apertamente. Il lavoro
  che *era* possibile — uno stack TLS 1.3 scritto da zero — è stato fatto e
  verificato.

Consideralo un punto di partenza ben documentato che richiede ancora una
compilazione reale e una prova su dispositivo, non un prodotto finito.
