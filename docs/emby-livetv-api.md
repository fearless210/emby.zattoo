# API plugin Live TV Emby actuelle

## Version de référence

- SDK Emby stable publié : `4.9.5.0` (commit SDK `bdd0dd7`).
- Template plugin minimal officiel : `netstandard2.0`.
- Package NuGet stable référencé par le template :
  `MediaBrowser.Server.Core` `4.9.1.90`.
- Une ligne 4.10 bêta existe, mais ne constitue pas la cible stable de ce projet.

Les assemblies plugin et Core ciblent donc `netstandard2.0`. Le plugin compile
contre `MediaBrowser.Server.Core` 4.9.1.90 ; cette version devra rester compatible
avec celle embarquée par le serveur Emby utilisé pour la validation réelle.

Sources officielles :

- [SDK Emby](https://github.com/MediaBrowser/Emby.Sdk)
- [développement de plugins](https://dev.emby.media/doc/plugins/dev/index.html)
- [référence `MediaBrowser.Controller.LiveTv`](https://dev.emby.media/reference/pluginapi/MediaBrowser.Controller.LiveTv.html)
- [package `MediaBrowser.Server.Core`](https://www.nuget.org/packages/MediaBrowser.Server.Core/4.9.1.90)

## Déclaration d'un tuner

L'interface actuelle est
`MediaBrowser.Controller.LiveTv.ITunerHost`. Elle expose notamment :

```csharp
Task<List<TunerHostInfo>> DiscoverDevices(
    int discoveryDurationMs,
    CancellationToken cancellationToken);

Task<List<ChannelInfo>> GetChannels(
    TunerHostInfo tuner,
    CancellationToken cancellationToken);

Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
    TunerHostInfo tuner,
    BaseItem dbChannel,
    string tunerChannelId,
    CancellationToken cancellationToken);

Task<ILiveStream> GetChannelStream(
    TunerHostInfo tuner,
    BaseItem dbChannel,
    string tunerChannelId,
    string streamId,
    List<ILiveStream> currentLiveStreams,
    CancellationToken cancellationToken);
```

Elle comprend aussi `RefreshChannels`, `GetProgramsAsync`, `OnSaved`, `OnDeleted`,
`SupportsGuideData`, `SupportsRemappingGuideData`, `GetDefaultConfiguration`,
`GetChannelIdPrefix` et la méthode actuellement orthographiée
`ValdidateOptions` dans l'API.

`BaseTunerHost : ITunerHost` fournit le préfixage des identifiants, le cache des
chaînes et une partie de l'adaptation. Pour le MVP, l'option recommandée est de
dériver de `BaseTunerHost` et d'implémenter au minimum :

```csharp
protected abstract Task<List<ChannelInfo>> GetChannelsInternal(...);
protected abstract Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(...);
```

Le MVP dérive finalement de `BaseTunerHost`. Les signatures ont été validées par
compilation contre `MediaBrowser.Server.Core` 4.9.1.90 et par inspection de
l'assembly officiel. Le chargement et le routage du tuner ont également été
validés sur Emby Server 4.9.5.0 sous Linux.

## Mapping des chaînes

`ChannelInfo` fournit actuellement les propriétés nécessaires :

```text
Id                  identifiant Emby
TunerChannelId      identifiant du fournisseur
Name                nom affiché
Number              numéro affiché
ImageUrl             logo téléchargeable
IsFavorite          état favori nullable
ChannelType         TV ou radio
```

Mapping prévu :

```text
ZattooChannel.Id (cid)  → TunerChannelId = cid
                         → Id dérivé via BaseTunerHost
ZattooChannel.Name      → Name
ZattooChannel.Number    → Number
ZattooChannel.LogoUrl   → ImageUrl
ZattooChannel.IsFavorite→ IsFavorite
```

Le `cid` sera la seule base d'identité. L'index de la liste n'entrera jamais dans
l'identifiant stable.

## Création d'une source Live

`MediaSourceInfo` se trouve dans `MediaBrowser.Model.Dto`. Les propriétés utiles
à vérifier pendant le spike de lecture sont notamment :

```text
Id, Name, Path, DirectStreamUrl
Protocol, Container, Formats
IsRemote, IsInfiniteStream
RequiredHttpHeaders
SupportsDirectPlay, SupportsDirectStream, SupportsTranscoding
RequiresOpening, RequiresClosing
LiveStreamId
```

Le tuner demande une URL Zattoo fraîche uniquement dans `ILiveStream.Open`,
jamais au chargement des chaînes. Il résout le master HLS7 en mémoire vers une
variante vidéo et l'audio par défaut, puis possède un processus FFmpeg serveur
qui remuxe sans réencodage vers MPEG-TS. La source communiquée à Emby emploie un
chemin virtuel `zattoo://<cid>` avant son ouverture. Une fois le processus de
remux démarré, ce chemin est remplacé par le point d'accès local Emby
`/LiveTv/LiveStreamFiles/<id>/stream.ts`, lequel consomme
`ILiveStream.CopyToAsync`. Aucun cookie ni URL signée n'est remis au client.

Le MVP annonce `mpegts`, désactive Direct Play, autorise Direct Stream et
Transcoding, et exige `Open`/`Close`. Cette combinaison est validée pour la
lecture de RTS 1 HD dans Emby Web. Le client Tizen et les changements répétés
restent à tester.

## Cycle de vie d'un stream

`MediaBrowser.Controller.Library.ILiveStream` expose actuellement :

```csharp
Task Open(CancellationToken openCancellationToken);
Task Close();
Task CopyToAsync(PipeWriter writer, CancellationToken cancellationToken);
Task CopyToAsync(
    Stream writer,
    DateTimeOffset? wallClockStartTime,
    Action<SegmentedStreamSegmentInfo> onSegmentWritten,
    CancellationToken cancellationToken);
```

ainsi que `MediaSource`, `UniqueId`, `OriginalStreamId`, `TunerHostId`,
`DateOpened`, `ConsumerCount`, `EnableStreamSharing` et `SupportsCopyTo`.

La stratégie retenue est l'adaptateur de remux serveur. `Close()` annule la copie,
demande l'arrêt propre de FFmpeg, attend au plus cinq secondes puis force sa
terminaison si nécessaire. Aucun fichier média temporaire n'est créé.

## EPG

`ITunerHost.GetProgramsAsync` retourne `Task<List<ProgramInfo>>` pour un tuner,
une `ChannelInfo`, une plage `DateTimeOffset` et un `CancellationToken`.
`ProgramInfo` comprend les champs nécessaires au mapping prévu : `ChannelId`,
`Id`, `Name`, `EpisodeTitle`, `Overview`, `StartDate`, `EndDate`, `Genres`,
`SeasonNumber`, `EpisodeNumber` et `ImageUrl`.

Le tuner retourne désormais `SupportsGuideData = true` et implémente
`GetProgramsInternal`. `BaseTunerHost` retire le préfixe propre à la source TV
avant cet appel, puis reconstruit pour chaque programme un identifiant Emby à
partir de `ShowId`, de l'heure de début et de l'identifiant complet de chaîne.
Le plugin fournit donc le `cid` brut au Core et l'identifiant Zattoo du programme
comme `ShowId` stable.

La tâche Emby **Actualiser le guide** choisit une plage commençant une heure
avant son exécution, puis appelle le fournisseur pour chaque chaîne. Emby utilise
sept jours si aucun réglage n'est défini et borne sa configuration entre un et
quatorze jours. Les `ProgramInfo` retournés sont enregistrés dans la base Emby ;
le plugin ne crée aucune tâche planifiée parallèle.

Le contrat `ITunerHost` ne contient aucun callback lorsqu'un utilisateur ouvre
la fiche d'un programme. Cette fiche est servie depuis la base Emby sans nouvel
appel au fournisseur de guide. L'ouverture d'un stream rappelle bien le tuner,
mais ne lui transmet que la chaîne et la source média, pas le programme affiché.
Un enrichissement déclenché à cet instant peut donc donner la priorité au
programme diffusé à l'heure courante, mais il ne peut pas modifier de façon
fiable la fiche déjà envoyée au client.

`ZattooGuideService` découpe la plage en fenêtres de cinq heures et les conserve
30 minutes. La réponse Zattoo couvrant plusieurs chaînes, ce cache empêche la
boucle Emby de répéter le même téléchargement pour chaque chaîne et mutualise
également les demandes concurrentes.

Un worker distinct enrichit ensuite les programmes incomplets sans retarder
`GetProgramsAsync`. Il utilise des lots de 20 espacés d'une seconde, déduplique
la file et les requêtes en vol, puis purge les entrées six heures après la fin
du programme. Les programmes courants et suivants précèdent les favoris, les
prochaines 24 heures, tandis que les programmes non favoris plus éloignés ne
sont pas enrichis par avance. Le guide de base reste disponible sur toute la
profondeur demandée. Une ouverture de stream redonne la priorité aux émissions
courante et suivante de la chaîne.

Les réponses sont écrites par lots dans un journal JSON du dossier de données
du plugin. Une empreinte des champs stables du programme évite tout nouvel appel
si le guide n'a pas changé, y compris après un redémarrage. Une empreinte
différente invalide seulement le programme concerné. Les réponses sans
description sont retentées plus tard, car elles peuvent être enrichies par le
fournisseur. Le journal est isolé par une portée hachée et compacté
périodiquement ; aucune dépendance native n'est ajoutée au paquet mono-DLL.

Une reconfiguration arrête le worker avant de mettre l'ancien client à la
retraite, sans interrompre les Live Streams déjà ouverts.

Les détails récupérés par le worker sont appliqués lors du prochain appel
`GetProgramsAsync`, donc lors d'une actualisation native ultérieure du guide.
Une mise à jour immédiate des lignes de programme déjà enregistrées demanderait
de dépendre des API internes de la base Emby et risquerait d'entrer en conflit
avec l'import en cours ; cette voie n'est pas retenue comme comportement normal.

## Incertitudes restantes

- comportement DASH direct de chaque client Emby ;
- comportement réel de fermeture de stream lors d'un arrêt ou changement de
  chaîne côté client ;
- stabilité lors d'une lecture longue et de changements répétés ;
- comportement du client Samsung Tizen ;
- profondeur EPG réellement publiée pour chaque chaîne et comportement des
  programmations d'enregistrement Emby.
