# Protocole Zattoo observé

Zattoo ne publie aucune API contractuelle : les chemins, champs et durées
décrits ici sont **observés**, et peuvent changer sans préavis.

La séquence et les endpoints ont d'abord été reconstitués à partir de la branche
`Piers` de [`rbuehlma/pvr.zattoo`](https://github.com/rbuehlma/pvr.zattoo),
version 22.2.3, commit `30c809e66f8eef22987fb518e89875031781dcae`. La section
[Champs réellement publiés](#champs-réellement-publiés) les complète par ce qu'un
compte reçoit vraiment, mesuré avec la commande `fields-survey` de l'outil de
diagnostic — plusieurs champs utiles manquaient à cette reconstitution.

Le présent projet réimplémente uniquement le protocole observé. Aucun code GPL
n'est copié et aucun mécanisme de déchiffrement ou de contournement DRM n'est
prévu.

## Séquence minimale

```text
GET /token.json
       ↓ client_app_token
POST /zapi/v3/session/hello
       ↓ cookie de session
GET /zapi/v3/session
       ↓ compte déjà associé ?
POST /zapi/v3/account/login (si nécessaire)
       ↓ power_guide_hash
GET /zapi/channels/favorites
GET /zapi/v3/cached/{power_guide_hash}/channels
```

Le même conteneur de cookies doit rester côté serveur pendant toute la séquence.
`pvr.zattoo` conserve notamment `beaker.session.id` et un identifiant `uuid`.
Le prototype utilise un `CookieContainer` privé et ne renvoie jamais ces cookies
à un client Emby.

## Opérations

### Récupération du jeton d'application

| Élément | Valeur observée |
|---|---|
| Méthode | `GET` |
| Chemin principal | `/token.json` |
| Paramètres | aucun |
| Cookie requis | aucun connu |
| Réponse utile | `success`, `session_token` |
| Validité | non documentée ; considéré comme éphémère et rechargé à la création de session |
| Erreurs | si le JSON n'est pas exploitable, recherche de `window.appToken` dans `/login`, puis d'un fichier `token-*.json` référencé par le bundle `app-*.js` |

`session_token` est sensible et ne doit jamais être journalisé.

### Initialisation de session (`hello`)

| Élément | Valeur observée |
|---|---|
| Méthode | `POST`, `application/x-www-form-urlencoded` |
| Chemin | `/zapi/v3/session/hello` |
| Paramètres | `lang`, `app_version`, `client_app_token`, `uuid`, `format=json` |
| Cookie requis | `uuid` envoyé par le client ; le serveur peut définir `beaker.session.id` |
| Réponse utile | `active` |
| Validité | non documentée ; liée au cookie de session |
| Erreurs | réponse non-2xx, JSON invalide ou `active != true` : session non initialisée |

Le prototype annonce sa propre identité HTTP (`Emby.Zattoo`) et utilise le flux
web observé. Il ne prétend pas être un téléviseur ou un appareil particulier.
Le client maintenu dans Streamlink utilise actuellement `app_version=3.2120.1`,
`Referer` vers le fournisseur et `X-Requested-With: XMLHttpRequest`; le Core
reprend ces paramètres de protocole web.

### Lecture de session

| Élément | Valeur observée |
|---|---|
| Méthode | `GET` |
| Chemin | `/zapi/v3/session` |
| Paramètres | aucun |
| Cookies requis | session courante et `uuid` |
| Réponse utile | `active`, `account`, `current_country`, `account.service_country`, `nonlive`, `power_guide_hash` |
| Validité | non documentée |
| Erreurs | `401`/`403` : session à invalider ; autre non-2xx ou `active != true` : échec d'initialisation |

Une valeur `account: null` indique qu'une authentification de compte est encore
nécessaire.

### Authentification du compte

| Élément | Valeur observée |
|---|---|
| Méthode | `POST`, `application/x-www-form-urlencoded` |
| Chemin | `/zapi/v3/account/login` |
| Paramètres | `login`, `password`, `format=json`, `remember=true` |
| Cookies requis | `uuid`; le serveur établit/actualise la session |
| Réponse utile | `active`, `account`, `power_guide_hash`, informations de pays et capacités |
| Validité | non documentée ; portée par les cookies |
| Erreurs | `401`/`403` ou `active != true` : identifiants refusés/session inutilisable ; `429` : limitation de débit |

Le corps de cette requête ne doit jamais être tracé. Les identifiants restent
uniquement en mémoire dans le processus serveur. Le cookie initialisé par
`session/hello` est conservé pour cette requête, conformément au flux web courant.

### Favoris

| Élément | Valeur observée |
|---|---|
| Méthode | `GET` |
| Chemin | `/zapi/channels/favorites` |
| Paramètres | aucun |
| Cookies requis | session authentifiée |
| Réponse utile | `success`, tableau `favorites` de `cid` |
| Validité | non documentée ; ne pas considérer la réponse comme permanente |
| Erreurs | une seule réauthentification et un seul nouvel essai pour `401`/`403` ; aucun retry infini |

### Chaînes

| Élément | Valeur observée |
|---|---|
| Méthode | `GET` |
| Chemin | `/zapi/v3/cached/{power_guide_hash}/channels` |
| Paramètres | `power_guide_hash` dans le chemin |
| Cookies requis | session authentifiée |
| Réponse utile | `groups`, `channels[].cid`, `group_index`, `recording`, `qualities[]` |
| Champs qualité | `availability`, `level`, `title`, `logo_white_84`, `drm_required` |
| Validité | cache côté fournisseur, durée non documentée ; le hash est renouvelé avec la session |
| Erreurs | même politique bornée `401`/`403`; JSON sans tableau `channels` : erreur de protocole |

Le `cid` est l'identifiant stable à conserver. Un index ou la position courante
ne doit pas servir d'identité. Pour Milestone 1, les numéros sont attribués dans
l'ordre de la réponse et les favoris sont exposés par `IsFavorite`.

### Guide EPG

| Élément | Valeur observée |
|---|---|
| Méthode | `GET` |
| Chemin | `/zapi/v3/cached/{power_guide_hash}/guide` |
| Paramètres | `start`, `end` (timestamps Unix), `format=json` |
| Cookies requis | session authentifiée |
| Réponse utile | objet `channels`, puis programmes par `cid`; champs courts `id`, `s`, `e`, `t`, `et`, `g`, `i_t` |
| Validité observée dans la référence | requêtes découpées en fenêtres de cinq heures ; durée contractuelle non documentée |
| Erreurs | JSON invalide/absence de chaîne : plage non chargée ; `401`/`403` doit déclencher au plus une réauthentification |

Le Core implémente cette opération dans `ZattooGuideService`. La plage demandée
est couverte par des fenêtres fixes de cinq heures. Chaque fenêtre contient les
programmes de plusieurs chaînes et reste en mémoire pendant 30 minutes. Emby
demandant son guide chaîne par chaîne, cette organisation permet aux chaînes
suivantes de réutiliser la même réponse. Un sémaphore empêche deux demandes
concurrentes de télécharger simultanément une fenêtre absente du cache.

Les deux formes observées pour `channels` sont acceptées : objet indexé par
`cid`, et ancien tableau d'objets contenant `cid` et `programs`. Les entrées
malformées sont ignorées individuellement ; un document sans collection de
chaînes est rejeté. Les programmes sont filtrés sur la plage exacte, dédupliqués
et triés avant leur transmission à Emby.

Les champs détaillés `d`, `s_no`, `e_no` et `i_url` sont utilisés lorsqu'ils
sont présents directement dans le guide. L'image `i_t` est transformée en URL
HTTPS Zattoo lorsqu'aucune URL complète n'est fournie.

Endpoint de détails complémentaires observé :
`GET /zapi/v2/cached/program/power_details/{power_guide_hash}` avec
`complete=True&program_ids=<liste>`. La réponse fournit notamment `d`, `s_no` et
`e_no`. Le client Core accepte un lot dédupliqué de 20 identifiants au maximum,
renouvelle la session une seule fois après `401`/`403` et rejette un document
invalide. Un premier appel réel contenant 100 identifiants n'a retourné que
20 détails. Cette taille correspond aussi au lot utilisé par le chargeur de
détails progressif de l'implémentation PVR de référence.

La commande `epg-endpoint-survey` compare le contenu exploitable de ce guide v3
à `/zapi/v2/cached/program/power_guide/{power_guide_hash}` sur une fenêtre
identique. Elle ne journalise que des compteurs anonymes. Elle sert à déterminer
si le guide v2 fournit directement assez de descriptions pour éviter une part
des appels à `power_details`.

Le sondage réel sur cinq heures a trouvé exactement les mêmes 3 840 programmes
dans les deux réponses, avec les mêmes titres d'épisode, genres, numéros et
images, mais aucune description directe dans l'une ou l'autre. Le v2 était
légèrement plus volumineux et plus lent sur cet échantillon. Le plugin conserve
donc le guide v3 et utilise `power_details` pour les descriptions manquantes.

La commande `epg-details-survey` utilise jusqu'à cinq lots et ne révèle aucun
contenu. Dans Emby, le chargement exhaustif synchrone est remplacé par un worker
progressif : il traite les lots de 20 à une seconde d'intervalle, sans bloquer
la tâche native du guide. Les programmes courants et suivants passent en
premier, puis les chaînes favorites et les prochaines 24 heures. Le reste du
guide n'est pas enrichi par avance, mais le guide de base conserve toute la
profondeur demandée. L'ouverture d'un stream replace le programme courant et le
suivant de la chaîne en tête de file.

Le cache retient les détails retournés ainsi que les réponses incomplètes. Il
est enregistré dans un journal JSON local, isolé par une portée de compte
hachée et compacté périodiquement. L'empreinte du programme permet de réutiliser
une entrée après un redémarrage lorsque le guide de base n'a pas changé. Les
requêtes déjà en vol sont dédupliquées ; une donnée nouvelle ou modifiée est
rechargée, tandis qu'une réponse sans description est retentée plus tard. Une
entrée est supprimée six heures après la fin du programme.

Le sondage réel en cinq lots espacés a retourné les 100 détails demandés sans
retry en 4,1 secondes. Parmi eux, 88 contenaient une description et 73 au moins
un genre. Aucun ne contenait de numéro de saison ou d'épisode dans cet
échantillon. En extrapolant la limite de 20 éléments aux 114 740 programmes du
guide sur sept jours, un chargement exhaustif nécessiterait 5 737 requêtes et
environ 96 minutes à la cadence prudente d'un lot par seconde. Il ne doit donc
pas bloquer la tâche native Emby et n'est pas exécuté : la fenêtre glissante et
les favoris bornent le volume réellement enrichi.

### Stream Live

| Élément | Valeur observée |
|---|---|
| Méthode | `POST`, `application/x-www-form-urlencoded` |
| Chemin MVP actuel | `/zapi/watch` |
| Chemin Kodi historique | `/zapi/watch/live/{cid}` |
| Paramètres MVP | `cid`, `quality`, `stream_type`, `https_watch_urls=true`, `format=json` |
| Types observés | `dash` sans DRM ; `dash_widevine` avec DRM |
| Cookies requis | session authentifiée |
| Réponse utile | `stream.url`, `stream.watch_urls[].url`, `maxrate`, `license_url`, `drm_limit_applied` |
| Validité | URL signée et éphémère ; toujours la demander juste avant lecture |
| Erreurs | `401`/`403` : un renouvellement + un retry ; absence de stream : indisponible ; DRM obligatoire : non pris en charge |

Le catalogue de chaînes expose déjà `drm_required` par qualité. Le Core conserve
les qualités disponibles avec la chaîne et applique deux chemins :

- `GetStreamOptionsAsync(cid)` décrit toutes les qualités disponibles, marque les
  qualités DRM `Unsupported` sans demander leur URL et ouvre les seules options
  non DRM au moment de cette commande explicite ;
- `GetStreamAsync(cid, préférence)` filtre d'abord DRM et préférence, puis ne
  demande qu'une seule URL pour la lecture/probe.

Une demande non DRM envoie `stream_type=dash`, `stream_type=hls7` ou
`stream_type=hls` et exige des URLs HTTPS
avec `https_watch_urls=true`. Le projet ne construit
jamais `dash_widevine`, n'utilise pas `license_url` et rejette par précaution une
réponse qui contiendrait une URL de licence. L'URL de lecture doit être HTTPS,
reste seulement dans l'objet retourné et n'est ni journalisée ni mise en cache.
Le paramètre Kodi `timeshift=10800` n'est pas utilisé : le timeshift avancé est
hors MVP et sa fenêtre a provoqué des lectures accélérées suivies de fragments
futurs en `404` lors du test ffmpeg.

La réponse peut fournir `stream.url` ou `stream.watch_urls`. Comme la référence,
le prototype retient la première entrée `watch_urls` exploitable et lit son
`maxrate`; `stream.url` sert de fallback.

## Champs réellement publiés

Ce document décrivait au départ un protocole reconstitué à partir d'une
implémentation tierce. La commande `fields-survey` mesure désormais ce qu'un
compte reçoit vraiment, et plusieurs champs utiles n'y figuraient pas. Les
relevés ci-dessous proviennent d'un compte suisse ; ils varient selon
l'abonnement et la région.

### Catalogue des chaînes

| Champ | Usage |
| --- | --- |
| `cid` | Identifiant stable, seule base d'identité |
| `number` | **Numéro officiel de la chaîne**, publié pour toutes. Le plugin s'en sert plutôt que de la position dans la réponse, qui se décale au moindre ajout |
| `title` | Nom de la chaîne |
| `is_radio` | Distingue les stations de radio des chaînes de télévision |
| `group_index` | Index dans le tableau `groups`, qui porte les noms de bouquets |
| `recording` | Enregistrement autorisé par le fournisseur |
| `qualities[]` | `level`, `availability`, `drm_required`, `title`, `stream_types` |
| `qualities[].logo_white_84`, `logo_black_84` | Logos, en variantes claire et sombre, également disponibles en 42 pixels |

### Guide

| Champ | Usage |
| --- | --- |
| `id`, `s`, `e`, `t`, `et` | Diffusion : identifiant, début, fin, titre, sous-titre |
| `tms_id` | **Identifiant du contenu**, stable d'une diffusion à l'autre. Renseigné sur 98 % des programmes, il alimente le `ShowId` d'Emby dont dépend l'enregistrement de série |
| `c`, `c_ids` | Catégories, en libellés localisés et en identifiants numériques. Les identifiants observés : 1 Séries, 2 Enfants, 3 Information, 4 Sport, 5 Films, 6 Divertissement, 7 Documentaires |
| `ser_e` | Marque un programme de série |
| `g` | Genres, vocabulaire trop vaste pour être énuméré |
| `yp_r` | Classification d'âge, au format FSK sur le compte observé |
| `s_no`, `e_no` | Saison et épisode, renseignés sur environ un tiers des programmes |
| `d` | Description, absente du guide et obtenue par l'endpoint de détails |
| `i_url`, `i_t` | Image, en URL complète ou en jeton |

### Détails de programme

Les mêmes champs, plus `year`, `country`, `cast[]`, `crew[]` et `cr` — ces trois
derniers sans équivalent dans le contrat EPG d'Emby, donc inutilisés.

Le mapping s'appuie sur les **identifiants numériques** et jamais sur les
libellés, qui dépendent du compte.

## Langue des métadonnées

Le champ `lang` envoyé au `hello` ne pilote pas la langue des métadonnées. La
commande `fields-survey` a été exécutée deux fois sur le même compte, une fois
avec `lang=en` et une fois avec `lang=de` : le vocabulaire des catégories est
resté identique et en français dans les deux cas — `Information`, `Séries`,
`Documentaires`, `Divertissement`, `Enfants`, `Sport`, `Films` — avec les mêmes
identifiants numériques 1 à 7.

Zattoo localise donc d'après le compte et sa région, pas d'après le paramètre de
session. Un réglage de langue dans le plugin n'aurait aucun effet observable et
n'est volontairement pas proposé. La mesure reste reproductible en définissant
`ZATTOO_LANGUAGE` avant la commande.

La constatation porte sur les catégories et la classification d'âge, seuls
champs dont l'inventaire collecte les valeurs. Les titres et descriptions n'ont
pas été comparés, l'outil ne lisant jamais de contenu.

## Points encore inconnus

- durée réelle des cookies et du `power_guide_hash` ;
- fréquence de rotation du jeton d'application ;
- ordre contractuel et stabilité du tableau de chaînes ;
- comportement exact de `401`, `403` et `429` selon l'abonnement ;
- proportion réelle de chaînes non DRM pour le compte cible ;
- lisibilité effective des MPD retournés avec ffprobe/ffmpeg ;
- headers supplémentaires éventuellement nécessaires à la lecture d'un MPD.

Ces éléments nécessitent des tests d'intégration explicites avec un compte réel.
Ils ne sont pas inventés dans les tests unitaires.
