# Faisabilité — porte GO / NO-GO n°1

Statut : **GO — inventaire favorable et stream HLS7 non DRM
stable pendant cinq minutes**.

Le Milestone 2 est implémenté et validé avec le compte cible. FFmpeg 9.0 a lu et
remuxé le flux sélectionné sans réencodage pendant 300,7 secondes, sans fragment
manquant ni corruption. La porte GO / NO-GO n°1 est franchie.

## Résultat technique obtenu

```text
Catalogue Zattoo
        │
        ├── qualités disponibles + drm_required
        │        ├── DRM ─────────────> Unsupported, aucun appel Widevine
        │        └── non-DRM
        │               ↓ sélection Auto/1080p/720p/540p
        │          POST stream_type=hls7
        │               ↓ master HTTPS éphémère, jamais logué/caché
        │          sélection vidéo + audio par défaut
        │               ↓ playlists média transmises à ffmpeg
        ├── rapport DRM/non-DRM
        ├── ffprobe (diagnostic codecs/conteneur)
        └── ffmpeg -c copy vers sortie nulle (test de lecture/remux)
```

Le Core sait désormais :

- extraire les qualités, leur disponibilité et `drm_required` du catalogue ;
- calculer les statistiques sans générer des URLs de lecture pour toutes les
  chaînes ;
- filtrer strictement le DRM et sélectionner la meilleure qualité autorisée ;
- demander un flux DASH ou HLS7 non DRM immédiatement avant son utilisation ;
- résoudre un master HLS7 en une seule variante vidéo et sa piste audio par
  défaut, uniquement en mémoire ;
- invalider puis renouveler une seule fois la session après `401`/`403` ;
- rejeter une réponse contenant `license_url`, sans tentative de Widevine ;
- exécuter des probes bornés dans le temps et nettoyer le processus à
  l'annulation/au timeout ;
- assainir stdout/stderr des outils médias avant affichage.

## Rapport à produire avec le compte cible

Résultat réel de la commande `survey` :

```text
Total chaînes         : 493
Streams disponibles   : 282
Non-DRM               : 264
DRM uniquement        : 18
Sans stream disponible: 211
```

Ces nombres comptent des **chaînes**, à partir des qualités disponibles du
catalogue. Le rapport n'ouvre volontairement aucune URL signée. Il faut ensuite
valider au moins une chaîne souhaitée avec `probe`, puis `ffmpeg-test`.

Parmi les chaînes disposant d'un stream, 93,6 % proposent au moins une qualité
non DRM. Cela représente 53,5 % du catalogue total. L'inventaire franchit donc
la partie DRM de la porte GO / NO-GO, sous réserve que les chaînes effectivement
souhaitées appartiennent à cet ensemble.

## Validation réelle de la profondeur EPG

Résultat réel de la commande `epg-survey 14` :

```text
Chaînes demandées                 : 493
Chaînes avec un guide futur       : 491
Programmes futurs                 : 214 215
Horizon maximal observé           : 14,2 jours
Chaînes atteignant la cible - 6 h : 490
```

La source EPG franchit sa porte de faisabilité : 99,6 % des chaînes ont des
programmes futurs et 99,4 % du catalogue atteint presque toute la profondeur
demandée. Les deux chaînes sans guide et la chaîne dont l'horizon est plus
court ne bloquent pas l'import global.

L'horizon de 14,2 jours n'indique pas une requête excessive. Un programme
commençant avant la limite demandée peut se terminer après celle-ci et reste
correctement inclus dans la plage. Ce sondage valide les données publiées par
Zattoo et leur chargement par le Core. L'import dans la base Emby et la
planification sont validés séparément ci-dessous.

## Probe réel — RTS 1 HD

```text
Qualité catalogue : 720p DASH non DRM
ffprobe            : code 0 en 8,2 s
Vidéo principale   : H.264, 1280×720, 25 fps
Audio              : AAC, 48 kHz, stéréo
Sous-titres         : TTML disponible
Format              : DASH
Débit déclaré       : environ 5,26 Mbit/s
```

Le manifeste expose plusieurs représentations vidéo adaptatives (720p, 432p et
288p) et plusieurs pistes audio. Le test ffmpeg sélectionne une vidéo et un audio
afin de mesurer un scénario de lecture réaliste sans télécharger toutes les
variantes.

## Critères de décision

GO uniquement si :

- une proportion utile des chaînes visées apparaît non DRM dans `survey` ;
- `streams <chaîne>` retourne au moins une option `Usable` ;
- `probe <chaîne>` identifie au minimum une piste vidéo et une piste audio ;
- `ffmpeg-test <chaîne> 300` se termine avec le code 0, sans réencodage ;
- l'URL reste utilisable durant le test et une nouvelle demande fonctionne après
  expiration ou renouvellement de session.

NO-GO si toutes les chaînes utiles sont DRM-only ou si les MPD non DRM ne sont
pas lisibles durablement par les outils serveur. En cas de NO-GO, le plugin Emby
ne doit pas être construit.

## État de validation

| Contrôle | Résultat |
|---|---|
| Build Release | OK, zéro warning |
| Tests unitaires anonymisés | OK |
| Aucun appel Widevine dans le Core | OK |
| Sélection Auto/1080p/720p/540p | OK |
| Retry de stream borné | OK |
| Authentification réelle | OK |
| Chaînes réelles | OK, 493 |
| Rapport réel du compte | OK, 264 chaînes non DRM sur 282 disponibles |
| Option Live RTS 1 HD | OK, 720p DASH non DRM |
| Probe MPD réel | OK, H.264/AAC, code 0 |
| Probe HLS réel | OK, H.264/AAC, 4 variantes vidéo et 3 pistes audio |
| Test préliminaire `ffmpeg -c copy` | OK, 30 s média traitées en 16,8 s grâce au buffer |
| Test `ffmpeg -re -c copy` exploratoire | code 0, mais pacing artificiel écarté après retard audio observé |
| Test 300 s avec timeshift Kodi | code 0, mais nombreux fragments 404 : non concluant |
| Test 30 s sans timeshift, chemin Kodi | 25,6 s média, 8 fragments 404 : FAIL |
| Test 30 s DASH, chemin web actuel | 25,6 s média, 8 fragments 404 : FAIL |
| Test 30 s HLS avant filtrage d'entrée | 19,2 s média, aucun 404, paquets H.264 corrompus : FAIL |
| Test HLS7 avec variantes écartées côté entrée | 19,2 s média, 12 alertes de corruption, 36 avertissements MOOV : FAIL |
| Test HLS/MPEG-TS (`stream_type=hls`) | HTTP 403 après renouvellement : indisponible |
| Test 30 s HLS7, une playlist vidéo + audio par défaut | PASS : 30,4 s média, zéro 404, zéro corruption |
| Test 300 s HLS7, une playlist vidéo + audio par défaut | PASS : 300,7 s média en 303,6 s, vitesse 1,01×, zéro 404, zéro corruption ; 359 avertissements MOOV bénins |
| Profondeur EPG réelle sur 14 jours | PASS : 214 215 programmes, 491 chaînes couvertes sur 493, dont 490 atteignant la cible à six heures près |
| Chargement EPG réel sur 7 jours par le Core | PASS : 114 740 programmes en 27,7 s et 491 chaînes couvertes |
| Import EPG natif dans Emby | PASS : première tâche en 7 min 16 s, seconde en 6 min 04 s, sans erreur signalée |
| Planification et enregistrement DVR | PASS : programmation persistante, médiathèque créée, fichier produit et lisible, stream fermé proprement |
| Sondage des détails EPG | PASS : cinq lots espacés, 100 réponses sur 100 sans retry en 4,1 s, dont 88 descriptions et 73 genres |
| Comparaison des guides v2 et v3 | PASS : mêmes 3 840 programmes et mêmes métadonnées sur cinq heures, aucune description directe ; v3 conservé |
| Cache de détails persistant | PASS automatisé : données inchangées réutilisées après redémarrage, programme modifié rechargé et portée de compte isolée |
| Verdict GO / NO-GO | **GO** |

## Limites restantes

- Zattoo n'expose pas une API publique contractuelle ; les champs peuvent
  changer ;
- les hauteurs `fhd=1080`, `hd=720` et `sd=540` sont des correspondances de
  sélection ; largeur et débit restent inconnus si le fournisseur ne les donne
  pas ;
- la stratégie HLS7 observée reste valide au moins pendant le test réel de cinq
  minutes ; sa durée contractuelle n'est pas publiée par Zattoo ;
- les segments fMP4 produisent un avertissement `duplicated MOOV` récurrent que
  FFmpeg ignore ; aucune corruption ni dérive de vitesse n'a été observée, mais
  ce compteur devra rester surveillé pendant les tests Emby ;
- le plugin Emby, le remux permanent, la source EPG et le parcours DVR sont
  implémentés et validés ; l'enrichissement progressif et persistant des
  descriptions est également validé sur le serveur réel ;
- Replay, les enregistrements hébergés par Zattoo et Widevine restent absents.

La lecture, la profondeur EPG côté fournisseur, les actualisations natives sur
7 jours et le parcours DVR sont validés. Le chargeur de détails est désormais
incrémental, persistant, limité en débit et indépendant de la durée de la tâche
Emby. Une reprise réelle a réutilisé 34 826 détails déjà présents et n'en a
chargé que 213 supplémentaires. L'actualisation suivante a rendu les
descriptions visibles dans l'interface Emby.
