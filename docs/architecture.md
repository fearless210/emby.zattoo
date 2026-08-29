# Architecture et décisions techniques

Ce document explique comment le plugin fonctionne et pourquoi il est construit
ainsi. Il s'adresse à quelqu'un qui veut contribuer sans relire tout le code.
Le protocole du fournisseur est décrit à part, dans
[zattoo-api.md](zattoo-api.md).

## Structure du dépôt

```text
src/
├── Emby.Zattoo.Core/       Client, modèles et transport Zattoo, sans Emby
├── Emby.Zattoo.Plugin/     Tuner, flux Live et configuration Emby
└── Zattoo.Spike/           Outil de diagnostic en ligne de commande

tests/
├── Emby.Zattoo.Core.Tests/
└── Emby.Zattoo.Plugin.Tests/
```

Le **Core ne connaît pas Emby**. Il expose des modèles neutres et
`IZattooTransport` isole les appels HTTP, ce qui permet aux tests d'injecter un
transport en mémoire : aucun test ne contacte le fournisseur.

Le Plugin traduit ces modèles vers les types Emby. Ses sources incluent celles
du Core à la compilation, afin de livrer **une seule DLL** — Emby ne charge pas
d'assembly secondaire de façon fiable.

Le Core cible `netstandard2.0`, contrainte du serveur Emby. Les tests et l'outil
de diagnostic ciblent `net8.0`.

## Ce que l'API Emby impose

Ces points ont été établis en décompilant le SDK. Ils ne sont pas documentés
publiquement et plusieurs bugs sont nés de les avoir supposés.

### `BaseTunerHost` ne rappelle pas le plugin pour lire ses chaînes

`GetChannels` lit un cache mémoire, et à défaut le fichier
`livetv/tuner_<id>_channels`, qui **survit au redémarrage**. Seuls
`RefreshChannels` — l'actualisation explicite des chaînes — et
`ValdidateOptions` appellent `GetChannelsInternal`.

`GetProgramsAsync` appelle directement `GetProgramsInternal`, sans passer par
les chaînes.

Conséquence : après un redémarrage d'Emby, le plugin peut recevoir des requêtes
de guide et des ouvertures de flux **sans jamais avoir chargé son catalogue**.
Or il en dérive le filtre des chaînes importées, les favoris qui priorisent
l'enrichissement, et la capacité de flux simultanés. Le tuner garantit donc
lui-même le chargement du catalogue avant de servir ces requêtes.

### Emby vérifie `TunerCount` avant d'appeler le plugin

```csharp
int tunerCount = tuner.TunerCount;
if (tunerCount > 0 && currentLiveStreams.Count(...) >= tunerCount)
    throw new LiveTvConflictException(...);
```

La capacité détectée doit donc atteindre l'objet `TunerHostInfo` qu'Emby
détient, pas seulement le verrou interne du plugin — sans quoi un second flux
légitime est refusé avant que le plugin ne voie la demande.

### `ShowId` identifie le contenu, pas la diffusion

Emby le documente comme « un identifiant du contenu, identique quels que soient
l'heure et la chaîne ». C'est ce qui lui permet de reconnaître une rediffusion,
donc de faire fonctionner l'enregistrement de série. Le plugin y place
l'identifiant de contenu du fournisseur, et non celui de la diffusion.

Emby construit ensuite l'identifiant d'une entrée de guide à partir de
`ShowId`, de l'heure de début et de la chaîne : changer `ShowId` réécrit toutes
les lignes du guide une fois.

### `ILiveStream` n'est pas `IDisposable`

Emby appelle `Open` puis `Close`, jamais `Dispose`. Tout ce qui doit être
libéré l'est dans `Close`.

### Emby se rattache plusieurs fois au même flux

La détection des pistes lit le flux avant que le transcodage ne le lise à son
tour. Un flux qui n'accepterait qu'un seul consommateur renvoie donc une erreur
au second — qu'Emby transforme en `HTTP 500` sans autre explication.

### Emby ne rapporte au client qu'une erreur générique

Sur le point d'accès Live Stream interne, la cause réelle d'un échec n'apparaît
nulle part si le plugin ne la journalise pas lui-même. C'est pourquoi chaque
rattachement est numéroté, chronométré et journalisé.

## Cycle de vie d'un flux

1. Emby demande une source média : le plugin renvoie une source virtuelle
   `zattoo://<cid>`, sans URL distante.
2. Emby ouvre le flux. Le plugin prend un jeton de capacité, demande une URL
   HLS fraîche, résout le manifeste en mémoire pour retenir **une** variante
   vidéo et l'audio par défaut, puis démarre FFmpeg en copie de flux.
3. La source média bascule alors vers le point d'accès local d'Emby
   `/LiveTv/LiveStreamFiles/<id>/stream.ts`, qui consomme `CopyToAsync`.
4. Les consommateurs sont servis **l'un après l'autre** sur le même tube : un
   nouveau reprend le flux à sa position courante, ce qui est le comportement
   attendu en direct. Deux consommateurs simultanés restent refusés, un tube ne
   se lisant pas deux fois à la fois.
5. `Close` arrête FFmpeg proprement, puis le tue après cinq secondes, et rend
   le jeton de capacité.

Une ouverture qui échoue ne doit **rien** laisser derrière elle : Emby n'appelle
pas `Close` sur un flux qui n'a pas ouvert, donc un FFmpeg déjà démarré
continuerait indéfiniment à tirer le flux du fournisseur.

### Déclarer ce que contient le flux

Le plugin publie le débit, la résolution et les codecs de la variante retenue
dans `MediaSourceInfo`. Sans cela, Emby n'a aucun débit source pour brider un
transcodage et retient le plafond annoncé par le client — un téléviseur
annonçant 200 Mbit/s a ainsi conduit l'encodeur à produire un flux de niveau
H.264 6.1, refusé par l'appareil lui-même.

Déclarer les pistes évite aussi qu'Emby suppose un flux entrelacé et applique
un désentrelacement inutile, coûteux en CPU et en latence de démarrage.

## Le guide

Emby décide de la plage et interroge le plugin **chaîne par chaîne**. Le Core
découpe cette plage en fenêtres de cinq heures et conserve chaque fenêtre trente
minutes : une réponse du fournisseur couvrant plusieurs chaînes, les chaînes
suivantes réutilisent la même donnée au lieu de la retélécharger.

Les descriptions et genres manquants sont chargés par un worker séparé, par lots
espacés, avec une file priorisée — programme courant, suivant, chaînes favorites,
prochaines 24 heures. Le guide de base n'attend jamais ce worker.

Les détails sont conservés dans un journal JSON local au dossier de données du
plugin. Une empreinte calculée sur les champs stables du programme permet de ne
pas recharger ce qui n'a pas changé, y compris après un redémarrage. Le fichier
est isolé par un hash du compte, du fournisseur et de la langue ; il ne contient
ni identifiant de connexion ni URL signée.

## Gestion de la configuration

Une sauvegarde de la page du plugin incrémente un compteur de révision. Le tuner
le compare avant de reconstruire ses réglages, ce qui évite de relire la
configuration et de déchiffrer le mot de passe à chaque requête.

Un changement de configuration crée un nouveau client. L'ancien est mis à la
retraite avec une période de grâce de cinq minutes — de quoi laisser une
ouverture de flux en cours se terminer — puis libéré. L'arrêt et la libération
se font **hors du verrou** du tuner, car arrêter un client attend la fin du lot
d'enrichissement en cours.

La page de configuration recopie ses réglages par réflexion. Les recopier à la
main a déjà fait perdre silencieusement les réglages ajoutés ensuite.

## Sécurité

- le mot de passe est chiffré par le service de chiffrement d'Emby et remplacé
  par un masque avant tout retour au navigateur ;
- `SensitiveDataSanitizer` retire URL, jetons et en-têtes d'authentification des
  messages journalisés ;
- les fixtures de test utilisent des domaines invalides et des secrets
  synthétiques ; aucun test ne contacte le fournisseur ;
- l'outil de diagnostic n'affiche jamais d'URL signée, et son inventaire de
  champs ne collecte aucune valeur hors vocabulaires de catalogue.

## Contrôles avant de proposer un changement

```bash
dotnet restore Emby.Zattoo.sln
dotnet build Emby.Zattoo.sln --configuration Release --no-restore
dotnet test Emby.Zattoo.sln --configuration Release --no-build --no-restore
dotnet format Emby.Zattoo.sln --verify-no-changes --no-restore
```

La CI exécute exactement ces quatre contrôles. Le build traite les
avertissements comme des erreurs.

Deux classes restent non testables hors d'Emby : `ZattooTunerHost` exige un
`IServerApplicationHost`, et `ZattooLiveStream` exige un flux réel. La logique
risquée en est extraite dans des classes testables — file de retraite des
clients, porte de consommateurs, filtres de chaînes, sélecteurs. Un changement
qui les touche doit dire explicitement ce qui n'est pas couvert.
