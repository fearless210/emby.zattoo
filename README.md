# Emby.Zattoo

**Regarder les chaînes non-DRM d'un compte Zattoo depuis Emby, comme n'importe
quelle autre source Live TV.** Le plugin s'exécute entièrement côté serveur :
il ouvre le flux Zattoo, choisit une piste vidéo et une piste audio, puis les
remuxe en MPEG-TS pour les clients Emby, sans réencodage.

> **Projet non officiel**, sans aucun lien avec Zattoo ni avec
> Emby. Il ne déchiffre aucune chaîne protégée et ne contourne aucun DRM.

```text
Compte Zattoo
      │  authentification, catalogue, guide
      ▼
Plugin Emby.Zattoo                        (tuner Live TV + fournisseur EPG)
      │  flux HLS non-DRM
      ▼
FFmpeg côté serveur, en copie de flux
      │  MPEG-TS
      ▼
Emby, puis n'importe quel client Emby
```

Les identifiants, les cookies et les URL signées ne quittent jamais le serveur.

## Ce que le plugin apporte

| | |
| --- | --- |
| **Chaînes** | Import des noms, numéros officiels, favoris, logos et bouquets, en ne retenant par défaut que les chaînes réellement lisibles sans DRM |
| **Lecture** | Flux HLS obtenu à la demande et remuxé en MPEG-TS, sans réencodage, avec reconnexion automatique en cas d'incident réseau |
| **Qualité** | Meilleure qualité non-DRM disponible, ou plafond manuel `1080p`, `720p`, `540p` |
| **Guide TV** | EPG jusqu'à 14 jours, limité aux chaînes importées, avec descriptions et genres chargés progressivement en arrière-plan |
| **Grille Emby** | Rubriques Films, Séries, Sport, Jeunesse et Actualités remplies, enregistrement de séries fonctionnel, année et classification d'âge |
| **Enregistrement** | Le DVR d'Emby fonctionne normalement ; le plugin adapte le nombre de tuners à la capacité du compte, pour regarder une chaîne pendant qu'une autre s'enregistre |
| **Sécurité** | Mot de passe chiffré par Emby, secret masqué dans l'interface, données sensibles retirées des journaux |

**Non pris en charge** : le Replay, les enregistrements hébergés par Zattoo, et
toute chaîne nécessitant un DRM.

## État

Validé sur Emby 4.9.5.0 sous Linux avec un compte réel : lecture sur Emby Web,
iOS et Samsung Tizen, transcodage matériel compris ; enregistrement de 2 h 10
mené à terme et décodé sans une seule erreur ; actualisation du guide en une
minute environ après le premier import.

Les chiffres du catalogue dépendent de l'abonnement et de la région. Sur le
compte de validation : 493 chaînes publiées, dont 264 lisibles sans DRM.

## Installation

### Prérequis

- un serveur Emby 4.9.x sous Linux, avec un abonnement Emby Premiere actif —
  les fonctions Live TV et DVR d'Emby en dépendent ;
- un compte Zattoo valide ;
- un accès HTTPS sortant vers Zattoo.

FFmpeg n'a pas à être installé séparément : le plugin utilise celui qu'Emby
embarque et lance déjà lui-même.

### 1. Installer la DLL

Télécharger `Emby.Zattoo.dll` depuis la
[dernière release](https://github.com/fearless210/emby.zattoo/releases/latest).
Le fichier `SHA256SUMS.txt` permet d'en vérifier l'intégrité.

1. Arrêter Emby Server.
2. Copier `Emby.Zattoo.dll` dans le dossier `plugins` du répertoire de données
   Emby — sur une installation Debian ou Ubuntu classique,
   `/var/lib/emby/plugins`.
3. Vérifier que l'utilisateur qui exécute Emby peut lire le fichier.
4. Redémarrer Emby Server, puis vérifier la présence de **Zattoo Live TV** dans
   **Dashboard → Plugins**.

Le plugin tient dans cette unique DLL ; aucun fichier annexe n'est à copier.

### 2. Configurer le compte

Ouvrir **Dashboard → Plugins → Zattoo Live TV**. **Deux réglages seulement sont
à renseigner** ; tous les autres ont une valeur par défaut qui convient à une
installation normale.

| Réglage | Par défaut | Faut-il y toucher ? |
| --- | --- | --- |
| **Zattoo username** | vide | **Oui.** Adresse e-mail ou nom du compte |
| **Zattoo password** | vide | **Oui.** Chiffré par Emby, jamais renvoyé au navigateur |
| Preferred quality | `Auto` | Non. `Auto` prend la meilleure qualité non-DRM du compte. Un plafond sert à limiter la bande passante |
| Channel import mode | `Playable channels only` | Non. Les autres modes servent au diagnostic, ou conservent les chaînes temporairement indisponibles |
| Channel groups | vide | Seulement pour n'importer que certains bouquets. Vide importe tout. Les noms disponibles sont écrits dans le journal après chaque actualisation des chaînes |
| Guide days | `0` | Seulement pour imposer la profondeur du guide, de 1 à 14 jours. `0` laisse le réglage Live TV d'Emby décider, ce qui donne sept jours sur `Auto` |
| Enrich guide descriptions | activé | Non. Charge en arrière-plan les descriptions et genres manquants, sans retarder l'actualisation du guide |
| FFmpeg executable | vide | Non. Vide signifie « utiliser le FFmpeg d'Emby », ce qui fonctionne y compris en conteneur |
| Provider URL | `https://zattoo.com/` | Non, sauf compte d'un revendeur compatible |
| Zattoo web application version | valeur proposée | Non, sauf diagnostic particulier |

Enregistrer. Le mot de passe s'affiche ensuite masqué : c'est normal, il n'est
jamais renvoyé au navigateur.

### 3. Ajouter la source TV

Ouvrir **Live TV → Tuner Devices → Add**, choisir **Zattoo**, valider, puis
lancer une actualisation des chaînes.

Deux options d'Emby, et non du plugin, méritent l'attention :

- **Import favorites only** limite l'import aux favoris définis dans Zattoo ;
- le nombre de jours de guide se règle dans les paramètres Live TV d'Emby, entre
  1 et 14. Sur `Auto`, Emby retient sept jours.

Lancer enfin la tâche **Actualiser le guide**. Le premier passage importe le
guide de base ; les descriptions détaillées se complètent progressivement en
arrière-plan et apparaissent aux actualisations suivantes.

### Mettre à jour

Remplacer la DLL et redémarrer suffit. Les identifiants, les réglages, la
source TV et le cache du guide sont conservés.

## Vérifier et dépanner

Après une actualisation des chaînes, le journal du serveur doit contenir :

```text
Loaded 264 of 493 Zattoo channels using PlayableOnly import mode.
Zattoo account capabilities: 264 playable channel(s), ...
Zattoo channel groups available: ...
```

À l'ouverture d'une chaîne :

```text
Opening Zattoo channel RTS 1 HD.
Zattoo live stream consumer 1 attached for channel RTS 1 HD.
```

| Symptôme | Piste |
| --- | --- |
| Aucune chaîne après l'import | Vérifier le mode d'import et, s'il est renseigné, le champ **Channel groups** : un nom de bouquet inexact ne ramène rien |
| Une chaîne refuse de s'ouvrir | Le journal nomme la cause. Une chaîne disponible uniquement en DRM ne sera jamais lisible |
| Erreur de lecture côté client | Emby ne renvoie au client qu'une erreur générique ; la cause réelle n'apparaît que dans le journal du serveur, préfixée `Zattoo` |
| Guide vide ou trop court | La profondeur vient d'Emby, pas du plugin. Voir le réglage **Guide days** |
| Second flux refusé | La capacité vient du compte Zattoo. Le journal indique la limite détectée et si elle est estimée |

Ne publiez jamais un journal Emby brut : il contient des clés d'API et des
jetons d'appareil.

## Limites et responsabilités

- aucun support ni contournement de DRM ;
- usage réservé à un compte Zattoo personnel valide, dans les régions et sur les
  contenus autorisés par l'abonnement ;
- aucun partage d'identifiants, contournement géographique ni redistribution ;
- les URL signées restent invisibles dans les journaux, mais peuvent apparaître
  dans la ligne de commande du processus FFmpeg au niveau du système ;
- les endpoints Zattoo utilisés sont observés, non documentés publiquement, et
  peuvent changer sans préavis ;
- compatibilité prévue pour Emby 4.9 uniquement à ce stade.

Avant toute utilisation ou redistribution, consultez les
[conditions Zattoo](https://zattoo.com/ch/fr/company/terms) applicables à votre
compte et la
[politique de développement des plugins Emby](https://dev.emby.media/doc/plugins/dev/Development-Policy.html).
La licence de ce dépôt ne couvre que son propre code : elle n'accorde aucun
droit sur les services, contenus, API ou marques de Zattoo et d'Emby.

## Contribuer

- [Architecture et décisions techniques](docs/architecture.md) — comment le
  plugin fonctionne, ce que l'API Emby impose, et les pièges déjà rencontrés ;
- [Protocole Zattoo observé](docs/zattoo-api.md) — les endpoints et les champs
  réellement publiés ;
- [Outil de diagnostic](docs/diagnostics.md) — tester un compte, un flux ou le
  guide sans passer par Emby ;
- [Règles de contribution](CONTRIBUTING.md), notamment sur les données
  sensibles ;
- [Notes de version](docs/releases/) — le détail de chaque version publiée.

```bash
dotnet build Emby.Zattoo.sln --configuration Release
```

Le build Release produit `artifacts/Emby.Zattoo/Emby.Zattoo.dll`. Le Core cible
`netstandard2.0` pour rester compatible avec le serveur Emby ; les tests et
l'outil de diagnostic ciblent `net8.0`. Aucun test ne contacte Zattoo.

## Licence

[Mozilla Public License 2.0](LICENSE). Vous pouvez utiliser, modifier, forker et
intégrer ce projet. Si vous redistribuez des fichiers couverts que vous avez
modifiés, leur code source doit rester disponible sous MPL-2.0.

Le projet [pvr.zattoo](https://github.com/rbuehlma/pvr.zattoo) a servi de
référence fonctionnelle pour comprendre le domaine. Emby.Zattoo est une
implémentation C# indépendante, et non un portage.
