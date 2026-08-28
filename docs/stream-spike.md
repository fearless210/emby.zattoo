# Exécution du Stream Spike

Toutes les commandes lisent `ZATTOO_USERNAME` et `ZATTOO_PASSWORD` dans
l'environnement. Elles n'affichent ni les identifiants, ni les cookies, ni les
URLs signées.

## 0. Inventaire des champs publiés

```bash
dotnet run --project src/Zattoo.Spike --configuration Release -- fields-survey
```

La commande interroge les réponses chaînes, favoris, guide et détails, puis
liste les noms de champs, leur fréquence, la part de champs réellement remplis
et les types JSON observés. Aucune valeur n'est collectée, à l'exception d'une
liste restreinte de vocabulaires de catalogue — catégories, genres, niveaux de
qualité, classification d'âge. Au-delà de soixante valeurs distinctes, le champ
est déclaré comme n'étant pas un vocabulaire et sa collecte s'arrête, afin
qu'une entrée mal classée ne puisse pas divulguer de contenu.

La sortie est donc partageable telle quelle. Elle sert à construire une
fonctionnalité sur ce que le compte reçoit réellement plutôt que sur le
protocole reconstitué : c'est elle qui a révélé le numéro de chaîne officiel,
l'identifiant de contenu utilisé pour l'enregistrement de séries et les
identifiants de catégories.

Définir `ZATTOO_LANGUAGE` avant la commande permet de comparer ce qu'une langue
de session change réellement.

## 1. Inventaire sans URL de lecture

```powershell
dotnet run --project src/Zattoo.Spike --configuration Release -- channels
dotnet run --project src/Zattoo.Spike --configuration Release -- survey
dotnet run --project src/Zattoo.Spike --configuration Release -- epg-survey 14
dotnet run --project src/Zattoo.Spike --configuration Release -- epg-endpoint-survey 5
dotnet run --project src/Zattoo.Spike --configuration Release -- epg-details-survey 100
```

`channels` affiche le numéro, le nom et le `cid` stable. `survey` calcule la
répartition DRM depuis le catalogue et ne demande aucun stream Live.

`epg-survey [1-14]` demande la profondeur EPG indiquée pour toutes les chaînes,
en utilisant le même découpage et le même cache que le plugin. Il affiche le
nombre de programmes futurs, le nombre de chaînes couvertes, l'horizon maximal
observé et le nombre de chaînes atteignant la cible à six heures près. Les
résultats dépendent du compte, de la région et des données publiées par chaque
chaîne.

`epg-endpoint-survey [1-6]` compare sur une même fenêtre l'ancien endpoint v2
`power_guide` et l'endpoint v3 actuellement utilisé. Il affiche uniquement les
tailles, durées, nombres de programmes et taux de présence des métadonnées,
ainsi que le recouvrement entre les deux réponses. Aucun contenu éditorial,
identifiant ou corps JSON n'est affiché.

`epg-details-survey [1-100]` sélectionne des programmes futurs sur les six
prochaines heures et demande jusqu'à cinq lots de 20 détails, espacés d'une
seconde. Une erreur de transport déclenche un seul nouvel essai après deux
secondes. La commande affiche uniquement le nombre de réponses contenant une
description, un genre ou une numérotation saison/épisode, ainsi que la durée et
le nombre de retries. Aucun titre, texte descriptif, identifiant ou corps JSON
n'est affiché. La commande mesure l'intérêt et le coût de l'enrichissement avant
son activation dans Emby.

## 2. Options d'une chaîne

```powershell
dotnet run --project src/Zattoo.Spike --configuration Release -- streams 1
dotnet run --project src/Zattoo.Spike --configuration Release -- streams "RTS 1"
dotnet run --project src/Zattoo.Spike --configuration Release -- streams <cid>
```

Cette commande explicite demande une URL éphémère pour chaque qualité non DRM de
la chaîne. Les qualités DRM sont affichées `Unsupported` sans appel Widevine.
Aucune URL n'est imprimée.

## 3. Probe MPD

Installer `ffprobe` dans le `PATH`, ou définir `FFPROBE_PATH` vers l'exécutable,
puis lancer :

```powershell
dotnet run --project src/Zattoo.Spike --configuration Release -- probe <cid> auto dash
dotnet run --project src/Zattoo.Spike --configuration Release -- probe <cid> auto hls
dotnet run --project src/Zattoo.Spike --configuration Release -- probe <cid> auto hls-ts
dotnet run --project src/Zattoo.Spike --configuration Release -- probe <cid> 720p dash
```

Le timeout est fixé à 45 secondes. La sortie doit montrer une piste vidéo et une
piste audio et se terminer avec le code 0.

## 4. Test de remux MPEG-TS sans réencodage

Installer `ffmpeg` dans le `PATH`, ou définir `FFMPEG_PATH`, puis lancer d'abord
un test court et ensuite cinq minutes :

```powershell
dotnet run --project src/Zattoo.Spike --configuration Release -- ffmpeg-test <cid> 30 auto dash
dotnet run --project src/Zattoo.Spike --configuration Release -- ffmpeg-test <cid> 30 auto hls
dotnet run --project src/Zattoo.Spike --configuration Release -- ffmpeg-test <cid> 30 auto hls-ts
dotnet run --project src/Zattoo.Spike --configuration Release -- ffmpeg-test <cid> 300 auto hls-ts
```

L'outil conserve côté entrée la première représentation vidéo et la première
piste audio avec `-discard`, puis les sélectionne avec
`-map 0:v:0 -map 0:a:0?`. Les représentations adaptatives alternatives et les
sous-titres sont ainsi écartés avant le démultiplexage. Il applique `-c copy`,
muxe réellement en MPEG-TS puis jette le résultat dans le périphérique nul, sans
créer de fichier média. La durée demandée est mesurée à l'horloge murale à partir du
premier rapport de progression média ; le temps d'ouverture et de probe reste
mesuré séparément dans le temps total. Le runner envoie ensuite `q` à ffmpeg
pour une fermeture propre. `-re` n'est pas
utilisé sur cette entrée Live : la documentation ffmpeg avertit qu'une readrate
faible sur une vraie source Live peut provoquer du retard ou des pertes. Dans le
plugin, le consommateur de sortie applique naturellement la contre-pression. Le
timeout de secours vaut la durée plus 45 secondes. Le
dernier argument permet de comparer DASH (argument `dash`,
`stream_type=dash`), HLS7/fMP4 (argument `hls`, `stream_type=hls7`) et
HLS/MPEG-TS (argument `hls-ts`, `stream_type=hls`) tout en conservant le même
filtrage DRM strict.

Pour HLS7, le spike charge le master signé en mémoire, sélectionne une seule
playlist vidéo dans la hauteur demandée et la piste audio déclarée par défaut,
puis transmet uniquement ces playlists média à ffmpeg. Le master et ses URLs
éphémères ne sont ni affichés, ni écrits sur disque. Cette étape évite que le
démultiplexeur sonde toutes les variantes adaptatives au démarrage.

## Interprétation

- code 0 pour les deux outils : candidat technique au GO ;
- `Unsupported` : DRM détecté, aucun test ne doit être tenté ;
- `Unavailable` : le catalogue annonçait la qualité mais l'API n'a pas retourné
  d'URL exploitable ;
- code 124 : timeout ;
- code 127 : outil média absent.

Les erreurs d'outil sont assainies avant affichage, notamment lorsque ffmpeg
répète son entrée. Les URLs restent cependant visibles dans la ligne de commande
du processus enfant au niveau du système d'exploitation, limite inhérente à
l'appel direct de ffmpeg/ffprobe durant ce spike local.
