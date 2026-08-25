# Emby.Zattoo

**Emby.Zattoo transforme les chaînes non-DRM d'un compte Zattoo en source
Live TV pour Emby.** Le plugin s'exécute entièrement côté serveur : il ouvre le
flux Zattoo, sélectionne la vidéo et l'audio, puis les remuxe en MPEG-TS pour
les clients Emby, sans réencodage.

> **Projet non officiel et expérimental.** Il n'est affilié ni à Zattoo ni à
> Emby. Le transport a été validé pendant cinq minutes, mais le chargement et la
> lecture sur un serveur Emby Linux réel restent à tester.

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
| Chaînes | Import des noms, numéros, favoris et logos dans le tuner Emby |
| Lecture | URL HLS7 non-DRM obtenue à la demande, avec une variante vidéo et l'audio par défaut |
| Qualité | Sélection `Auto`, `1080p`, `720p` ou `540p` |
| Transport | Remux H.264/AAC vers MPEG-TS par FFmpeg avec `-c copy`, sans réencodage |
| Configuration | Page **Settings** native dans le tableau de bord Emby |
| Sécurité | Mot de passe chiffré par Emby, secret masqué dans l'interface et données sensibles retirées des logs |

L'EPG, le Replay, les enregistrements et les flux Widevine/DRM ne sont pas pris
en charge dans cette version.

## État du projet

La version actuelle est `0.2.0` et cible la branche stable Emby 4.9.

| Validation | État |
| --- | --- |
| Authentification et catalogue Zattoo | Validés sur un compte réel |
| Inventaire observé | 493 chaînes, dont 264 avec au moins un flux non-DRM |
| Lecture HLS et remux FFmpeg | Validés sur RTS 1 HD pendant 300,7 s, sans fragment manquant ni corruption |
| Compilation et tests automatisés | Validés sous .NET 8 ; les tests utilisent uniquement des données fictives |
| Chargement du plugin sur Emby Linux | À tester |
| Lecture Emby Web et changements de chaîne | À tester |
| Client Samsung Tizen et test longue durée | Étape ultérieure |

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

Télécharger `Emby.Zattoo-v0.2.0.zip` depuis la
[release v0.2.0](https://github.com/fearless210/emby.zattoo/releases/tag/v0.2.0),
puis extraire les deux DLL. Elles sont également proposées séparément dans les
assets de la release. Le fichier `SHA256SUMS.txt` permet d'en vérifier
l'intégrité.

Pour construire le plugin depuis les sources à la place, le SDK .NET 8 est
nécessaire :

```powershell
dotnet build .\Emby.Zattoo.sln --configuration Release
```

Sous une installation locale où `dotnet` n'est pas encore dans le `PATH`, la
même commande peut être lancée avec la fonction PowerShell `dotnet8` utilisée
pendant le développement.

Le build Release produit les deux fichiers à installer :

```text
artifacts/Emby.Zattoo/Emby.Zattoo.dll
artifacts/Emby.Zattoo/Emby.Zattoo.Core.dll
```

### 2. Copier les DLL sur le serveur

1. Arrêter Emby Server.
2. Copier **les deux DLL** directement dans le dossier `programdata/plugins` de
   l'installation Emby. Son emplacement dépend du paquet Linux, du conteneur
   Docker ou des volumes configurés.
3. Vérifier que l'utilisateur Emby peut lire les fichiers.
4. Redémarrer Emby Server.
5. Vérifier la présence de `Zattoo Live TV` dans **Dashboard → Plugins**.

Les assemblies Emby et .NET sont fournies par le serveur et ne doivent pas être
copiées depuis les paquets NuGet.

### 3. Configurer le plugin dans Emby

Ouvrir **Dashboard → Plugins → Zattoo Live TV → Settings**, puis renseigner :

| Paramètre | Valeur attendue |
| --- | --- |
| Zattoo username | Adresse e-mail ou nom du compte |
| Zattoo password | Mot de passe du compte ; il sera chiffré côté serveur |
| Preferred quality | `Auto`, `1080p`, `720p` ou `540p` |
| FFmpeg executable | Chemin absolu Linux recommandé, par exemple `/usr/bin/ffmpeg` |
| Provider URL | Conserver `https://zattoo.com/`, sauf compte d'un revendeur compatible |
| Zattoo web application version | Conserver la valeur proposée, sauf diagnostic particulier |

Enregistrer, puis ouvrir **Live TV → Tuner Devices → Add**, sélectionner
**Zattoo** et actualiser les chaînes. L'option Emby **Import favorites only** est
prise en charge. Le MVP limite le tuner à un flux simultané.

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

## Développement

### Compiler et tester

```bash
dotnet restore Emby.Zattoo.sln
dotnet build Emby.Zattoo.sln --configuration Release --no-restore
dotnet test Emby.Zattoo.sln --configuration Release --no-build --no-restore
dotnet format Emby.Zattoo.sln --verify-no-changes --no-restore
```

La CI GitHub exécute les mêmes contrôles sous Linux et publie les deux DLL comme
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
et les cookies, ce qui permet aux tests d'injecter un transport en mémoire.

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
