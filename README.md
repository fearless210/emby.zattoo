# Emby.Zattoo

**Emby.Zattoo transforme les chaînes non-DRM d'un compte Zattoo en source
Live TV pour Emby.** Le plugin s'exécute entièrement côté serveur : il ouvre le
flux Zattoo, sélectionne la vidéo et l'audio, puis les remuxe en MPEG-TS pour
les clients Emby, sans réencodage.

> **Projet non officiel et expérimental.** Il n'est affilié ni à Zattoo ni à
> Emby. Le chargement, la configuration, l'import des chaînes et la lecture
> continue ont été validés sur Emby Linux. La profondeur EPG a été validée avec
> un compte réel ; son import et le parcours d'enregistrement sont également
> validés dans Emby.

## Pourquoi ce projet ?

Zattoo fournit le catalogue et les flux TV associés à un compte, tandis qu'Emby
attend une source compatible avec son système de tuners Live TV. Ce plugin fait
le lien entre les deux : les chaînes apparaissent dans Emby et un flux lisible
par Emby est produit à chaque ouverture.

```text
Compte Zattoo
      │ authentification et catalogue
      ▼
Plugin Emby.Zattoo
      │ flux HLS non-DRM + sélection vidéo/audio
      ▼
FFmpeg côté serveur (`-c copy`)
      │ MPEG-TS
      ▼
Emby Web, puis clients Emby compatibles
```

Les identifiants, cookies et URL signées restent côté serveur. Le projet ne
déchiffre pas les chaînes protégées et ne contourne aucun DRM.

## Fonctionnalités

| Domaine | Ce que fournit le plugin |
| --- | --- |
| Compte | Initialisation et authentification Zattoo, avec un renouvellement de session après `401` ou `403` |
| Chaînes | Import des noms, numéros, favoris et logos, avec filtrage recommandé sur les seules chaînes réellement lisibles sans DRM |
| Lecture | URL HLS7 non-DRM obtenue à la demande, avec une variante vidéo et l'audio par défaut |
| Qualité | Sélection `Auto` de la meilleure qualité disponible non-DRM du compte, ou plafond manuel `1080p`, `720p` ou `540p` |
| Transport | Remux H.264/AAC vers MPEG-TS par FFmpeg avec `-c copy`, sans réencodage |
| Guide TV | EPG Zattoo limité aux chaînes importées, chargé par la tâche native Emby jusqu'à 14 jours, cache partagé et enrichissement persistant des descriptions |
| Configuration | Page **Settings** native dans le tableau de bord Emby |
| Sécurité | Mot de passe chiffré par Emby, secret masqué dans l'interface et données sensibles retirées des logs |

Le Replay, les enregistrements hébergés par Zattoo et les flux Widevine/DRM ne
sont pas pris en charge.

## État du projet

La version `1.0.0` est la première version fonctionnelle du projet et cible la
branche stable Emby 4.9. Elle réunit l'authentification, l'import des chaînes,
la lecture Live, le guide enrichi et le parcours DVR validés sur serveur réel.

| Validation | État |
| --- | --- |
| Authentification et catalogue Zattoo | Validés sur un compte réel |
| Inventaire observé | 493 chaînes, dont 264 avec au moins un flux non-DRM |
| Lecture HLS et remux FFmpeg | Validés sur RTS 1 HD pendant 300,7 s, sans fragment manquant ni corruption |
| Compilation et tests automatisés | Validés sous .NET 8 ; les tests utilisent uniquement des données fictives |
| Chargement, configuration et import sur Emby Linux | Validés sur Emby 4.9.5.0 |
| Routage des chaînes vers le tuner Emby | Validé sur Emby 4.9.5.0 |
| Exposition du remux via le point d'accès Live Stream interne | Validée sur Emby 4.9.5.0 |
| Lecture Emby Web | Validée sur RTS 1 HD |
| Arrêt et changements de chaîne | Validés sans processus FFmpeg résiduel |
| Lecture continue | Validée pendant 15 minutes sans coupure perçue |
| Profondeur EPG réelle jusqu'à 14 jours | Validée : 214 215 programmes futurs, 491 chaînes couvertes sur 493 |
| Import EPG par la tâche native Emby | Validé sur 7 jours et 114 740 programmes ; premier import en 7 min 16 s, second en 6 min 04 s |
| Détails EPG | Enrichissement persistant validé dans Emby ; descriptions visibles et seulement 213 détails chargés après reprise d'un cache existant |
| Planification et exécution d'un enregistrement | Validées avec création de la médiathèque et fichier lisible |

Les chiffres du catalogue dépendent de l'abonnement, de la région et du compte
utilisés. Voir le [rapport de faisabilité](docs/feasibility.md) pour le détail
des essais.

## Installation manuelle sur Emby

### Prérequis

- un serveur Emby 4.9.x sous Linux ;
- un compte Zattoo valide ;
- FFmpeg installé et exécutable par l'utilisateur qui lance Emby ;
- un accès HTTPS sortant vers Zattoo.

### 1. Télécharger le plugin

Télécharger `Emby.Zattoo-v1.1.0.zip` depuis la
[release v1.1.0](https://github.com/fearless210/emby.zattoo/releases/tag/v1.1.0),
puis extraire `Emby.Zattoo.dll`. La DLL est également proposée séparément dans
les assets de la release. Le fichier `SHA256SUMS.txt` permet d'en vérifier
l'intégrité.

Pour construire le plugin depuis les sources à la place, le SDK .NET 8 est
nécessaire :

```powershell
dotnet build .\Emby.Zattoo.sln --configuration Release
```

Sous une installation locale où `dotnet` n'est pas encore dans le `PATH`, la
même commande peut être lancée avec la fonction PowerShell `dotnet8` utilisée
pendant le développement.

Le build Release produit le fichier à installer :

```text
artifacts/Emby.Zattoo/Emby.Zattoo.dll
```

### 2. Copier la DLL sur le serveur

1. Arrêter Emby Server.
2. Supprimer toute ancienne copie de `Emby.Zattoo.Core.dll`, puis copier
   **`Emby.Zattoo.dll`** directement dans le dossier `programdata/plugins` de
   l'installation Emby. Son emplacement dépend du paquet Linux, du conteneur
   Docker ou des volumes configurés.
3. Vérifier que l'utilisateur Emby peut lire les fichiers.
4. Redémarrer Emby Server.
5. Vérifier la présence de `Zattoo Live TV` dans **Dashboard → Plugins**.

Lors d'une mise à niveau depuis la version `0.2.2`, supprimer puis recréer la
source TV **Zattoo** dans **Live TV** après le redémarrage. Cette opération force
Emby à remplacer les anciens identifiants de chaînes non préfixés. Les
identifiants Zattoo enregistrés dans la page du plugin sont conservés.

Les assemblies Emby et .NET sont fournies par le serveur et ne doivent pas être
copiées depuis les paquets NuGet.

### 3. Configurer le plugin dans Emby

Ouvrir **Dashboard → Plugins → Zattoo Live TV → Settings**, puis renseigner :

| Paramètre | Valeur attendue |
| --- | --- |
| Zattoo username | Adresse e-mail ou nom du compte |
| Zattoo password | Mot de passe du compte ; il sera chiffré côté serveur |
| Preferred quality | `Auto`, `1080p`, `720p` ou `540p` |
| Channel import mode | `Playable channels only` est recommandé ; les modes élargis servent au diagnostic ou conservent les chaînes temporairement indisponibles |
| Enrich guide descriptions | Recommandé ; charge progressivement les descriptions et genres manquants sans bloquer Emby |
| FFmpeg executable | Chemin absolu Linux recommandé, par exemple `/usr/bin/ffmpeg` |
| Provider URL | Conserver `https://zattoo.com/`, sauf compte d'un revendeur compatible |
| Zattoo web application version | Conserver la valeur proposée, sauf diagnostic particulier |

Enregistrer, puis ouvrir **Live TV → Tuner Devices → Add**, sélectionner
**Zattoo** et actualiser les chaînes. L'option Emby **Import favorites only** est
prise en charge. La tâche Emby **Actualiser le guide** appelle directement le
fournisseur EPG du tuner. Emby utilise sept jours par défaut et permet d'en
configurer jusqu'à quatorze. Le plugin ajuste le nombre de tuners à la capacité
du compte : cela permet, lorsque l'abonnement l'autorise, de regarder une chaîne
pendant qu'Emby en enregistre une autre. Il ne met pas en place de gestion
multi-utilisateur propre au plugin.

Le protocole de validation complet se trouve dans le
[guide d'installation et de test Emby](docs/emby-mvp.md).

## Fonctionnement

Le tuner expose à Emby une source virtuelle stable `zattoo://<cid>` plutôt
qu'une URL distante. À l'ouverture d'une chaîne, le plugin :

1. ouvre ou renouvelle la session Zattoo ;
2. demande une URL HLS non-DRM fraîche pour le `cid` concerné ;
3. résout le manifeste en mémoire et retient une vidéo et l'audio par défaut ;
4. lance FFmpeg côté serveur pour copier les pistes dans un conteneur MPEG-TS ;
5. transmet la sortie à Emby avec contre-pression ;
6. arrête le processus FFmpeg à la fermeture du flux.

Cette architecture évite d'envoyer les credentials, cookies ou URL Zattoo au
navigateur et aux clients Emby.

### Guide TV

Le plugin implémente le fournisseur de guide du tuner Emby. Lors de la tâche
**Actualiser le guide**, Emby demande les programmes chaîne par chaîne. Le Core
charge les données JSON Zattoo en fenêtres de cinq heures et conserve chaque
fenêtre pendant 30 minutes. Comme une réponse contient plusieurs chaînes, les
appels suivants réutilisent les données déjà chargées au lieu de solliciter le
fournisseur pour chaque chaîne. Seules les chaînes retenues par le mode d'import
et, le cas échéant, par **Import favorites only** sont matérialisées dans le
cache EPG et proposées à l'enrichissement détaillé.

Les capacités sont déterminées depuis les valeurs techniques de la session et
du catalogue, sans dépendre d'un nom commercial d'abonnement. Le mode `Auto`
choisit la meilleure qualité disponible non-DRM. La limite de flux simultanés
est utilisée à la fois par le tuner Emby et par un verrou interne au plugin. Si
le fournisseur ne publie pas directement cette limite, une valeur prudente est
inférée depuis ses limites numériques d'enregistrement ; cette origine est
indiquée dans les logs.

Les programmes sont filtrés sur la plage demandée, dédupliqués et transmis à
Emby avec un `ShowId` Zattoo stable. Emby construit ensuite son propre
identifiant, stocke le guide dans sa base et l'utilise pour la planification.
La profondeur effective reste limitée aux données publiées par Zattoo pour le
compte, la région et chaque chaîne.

Lorsque **Enrich guide descriptions** est activé, les programmes dont le résumé
ou les genres sont incomplets rejoignent une file d'arrière-plan. Le worker les
traite par lots de 20 espacés d'une seconde. L'ordre donne la priorité aux
programmes en cours et suivants, puis aux chaînes favorites et aux programmes
des prochaines 24 heures. Les programmes non favoris plus éloignés restent
disponibles dans le guide de base et ne sont enrichis que lorsqu'ils entrent
dans cette fenêtre glissante. L'ouverture d'une chaîne replace aussi son
programme courant et le suivant en tête de file, sans retarder la lecture.

Les détails sont conservés dans un journal JSON local au dossier de données du
plugin. Chaque programme reçoit une empreinte calculée à partir des données du
guide : si elle n'a pas changé après une actualisation ou un redémarrage, aucun
nouvel appel de détail n'est effectué. Seuls les programmes nouveaux ou modifiés
sont ajoutés. Une réponse encore sans description est retentée de manière
espacée, car Zattoo peut compléter ses métadonnées plus tard. Les entrées sont
supprimées six heures après la fin du programme et le journal est compacté
périodiquement.

Ce traitement ne ralentit pas la tâche native **Actualiser le guide**. Le
premier passage importe immédiatement le guide de base ; une actualisation Emby
ultérieure applique les descriptions déjà chargées. Le fichier de cache ne
contient ni identifiants de connexion ni URL signée, et sa portée de compte est
un hash. Les logs indiquent seulement des compteurs de progression, sans titre,
identifiant de programme ni corps de réponse.

## Développement

### Compiler et tester

```bash
dotnet restore Emby.Zattoo.sln
dotnet build Emby.Zattoo.sln --configuration Release --no-restore
dotnet test Emby.Zattoo.sln --configuration Release --no-build --no-restore
dotnet format Emby.Zattoo.sln --verify-no-changes --no-restore
```

La CI GitHub exécute les mêmes contrôles sous Linux et publie la DLL comme
artifact temporaire. Aucun test automatisé ne contacte Zattoo : les fixtures de
`tests/` sont entièrement fictives.

### Outil de diagnostic

`Zattoo.Spike` permet de tester le compte et le transport indépendamment
d'Emby. Les credentials sont fournis uniquement au processus courant :

```powershell
$env:ZATTOO_USERNAME = "adresse@example.com"
$env:ZATTOO_PASSWORD = "mot-de-passe"

dotnet run --project .\src\Zattoo.Spike -- channels
dotnet run --project .\src\Zattoo.Spike -- survey
dotnet run --project .\src\Zattoo.Spike -- epg-survey 14
dotnet run --project .\src\Zattoo.Spike -- epg-details-survey 100
dotnet run --project .\src\Zattoo.Spike -- streams tsr1
dotnet run --project .\src\Zattoo.Spike -- probe tsr1 auto hls
dotnet run --project .\src\Zattoo.Spike -- ffmpeg-test tsr1 30 auto hls

Remove-Item Env:ZATTOO_USERNAME, Env:ZATTOO_PASSWORD
```

Les sélecteurs acceptent un identifiant, un numéro ou le nom exact d'une
chaîne. Les URL signées ne sont jamais affichées. Le
[guide du Stream Spike](docs/stream-spike.md) décrit toutes les commandes et
leurs critères de réussite.

Les variables d'environnement concernent uniquement cet outil de diagnostic.
Le plugin installé utilise les credentials saisis dans son écran de
configuration Emby.

## Structure du dépôt

```text
src/
├── Emby.Zattoo.Core/       Client, modèles et transport Zattoo indépendants
├── Emby.Zattoo.Plugin/     Intégration tuner et Live Stream pour Emby
└── Zattoo.Spike/           CLI de diagnostic et de faisabilité

tests/
├── Emby.Zattoo.Core.Tests/
└── Emby.Zattoo.Plugin.Tests/

docs/                       Analyses, décisions et guides de validation
```

Le Core cible `netstandard2.0` afin de rester compatible avec le serveur Emby.
Le CLI et les tests ciblent `net8.0`. `IZattooTransport` isole les appels HTTP
et les cookies, ce qui permet aux tests d'injecter un transport en mémoire. Le
build du plugin inclut les sources du Core dans `Emby.Zattoo.dll` afin de ne pas
dépendre du chargement d'une assembly secondaire par Emby.

## Sécurité et limites

- aucun support ou contournement de Widevine/DRM ;
- utilisation réservée à un compte Zattoo personnel valide, dans les régions et
  sur les contenus autorisés par l'abonnement ;
- aucun partage d'identifiants, contournement géographique, contournement de
  contrôle d'accès ou redistribution des programmes ;
- aucune mise en cache des URL de lecture éphémères ;
- aucun mot de passe, cookie, token, corps d'authentification ou URL signée dans
  les messages d'erreur ;
- mot de passe du plugin chiffré avec le service de chiffrement Emby et remplacé
  par un masque avant retour au navigateur ;
- URL signées invisibles dans les logs, mais susceptibles d'apparaître dans la
  ligne de commande du processus FFmpeg au niveau du système d'exploitation ;
- endpoints Zattoo observés et non garantis comme API publique stable ;
- compatibilité prévue pour Emby 4.9 seulement à ce stade.

Ne publiez jamais de log Emby ou FFmpeg non expurgé. Les règles de contribution
et de gestion des données sensibles sont détaillées dans
[CONTRIBUTING.md](CONTRIBUTING.md).

La licence du dépôt porte uniquement sur le code d'Emby.Zattoo. Elle n'accorde
aucun droit sur le service, les contenus, les API ou les marques de Zattoo et
d'Emby. Chaque utilisateur reste responsable du respect de son abonnement, des
conditions des fournisseurs et du droit applicable.

Avant utilisation ou redistribution, consultez les
[conditions Zattoo](https://zattoo.com/ch/fr/company/terms) applicables à votre
compte ainsi que la
[politique de développement des plugins Emby](https://dev.emby.media/doc/plugins/dev/Development-Policy.html).
Le projet utilise des endpoints Zattoo non documentés publiquement ; sa licence
open source ne vaut pas approbation par Zattoo. Une autorisation écrite du
fournisseur reste la voie la plus sûre avant une diffusion large ou une demande
d'intégration au catalogue Emby.

## Documentation

- [Changelog](CHANGELOG.md)
- [Notes de la version 1.1.0](docs/releases/v1.1.0.md)
- [Notes de la version 1.0.0](docs/releases/v1.0.0.md)
- [Installation et validation du MVP Emby](docs/emby-mvp.md)
- [Rapport de faisabilité et décisions GO / NO-GO](docs/feasibility.md)
- [Guide du Stream Spike](docs/stream-spike.md)
- [Analyse de l'intégration Live TV Emby](docs/emby-livetv-api.md)
- [Analyse des échanges Zattoo](docs/zattoo-api.md)

## Références et avertissement

Le projet [pvr.zattoo](https://github.com/rbuehlma/pvr.zattoo) a servi de
référence fonctionnelle pour comprendre le domaine. Emby.Zattoo est une
implémentation C# indépendante destinée à l'API plugin Emby ; il ne s'agit pas
d'un port ligne par ligne.

Ce logiciel expérimental n'est ni approuvé ni maintenu par Zattoo ou Emby. Les
noms et marques appartiennent à leurs propriétaires respectifs. Son utilisation
doit respecter les conditions du fournisseur et la législation applicable.

## Licence

Ce projet est distribué sous [Mozilla Public License 2.0](LICENSE). Vous pouvez
l'utiliser, le modifier, le forker et l'intégrer à un projet plus large. Si vous
redistribuez des fichiers couverts que vous avez modifiés, leur code source doit
rester disponible sous MPL-2.0.
