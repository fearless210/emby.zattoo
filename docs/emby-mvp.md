# Emby MVP — installation et validation

Le plugin 1.1.1 compile contre le SDK Emby stable 4.9.5.0
et `MediaBrowser.Server.Core` 4.9.1.90. Il inclut une page de configuration
native Emby. Son chargement, sa configuration et l'import des chaînes ont été
validés sur Emby Linux. La lecture de RTS 1 HD, l'arrêt, le changement de chaîne,
la lecture continue et le parcours DVR fonctionnent dans Emby Web.

## Architecture du MVP

```text
Emby ZattooTunerHost
        │ cid stable, jamais un index
        ▼
ZattooClient partagé
        │ URL HLS7 fraîche, DRM exclu
        ▼
HlsManifestResolver (mémoire uniquement)
        │ une vidéo + audio par défaut
        ▼
ZattooLiveStream / FFmpeg -c copy
        │ MPEG-TS sur stdout avec contre-pression
        ▼
ILiveStream.CopyToAsync → Emby Server
```

Le client Emby ne reçoit ni cookies, ni credentials, ni URL Zattoo. La source
annoncée à Emby utilise seulement un chemin virtuel `zattoo://<cid>`. Le
processus FFmpeg appartient au Live Stream et est annulé, arrêté puis éliminé
par `Close()`.

## Fonctionnalités présentes

- assembly `Zattoo Live TV` compilé contre l'API Emby 4.9 ;
- page **Settings** générée nativement dans le tableau de bord Emby ;
- mot de passe chiffré par `IEncryptionManager` et masqué côté navigateur ;
- tuner `zattoo` dont la capacité est ajustée aux limites techniques du compte ;
- chaînes TV, numéros, favoris et logos ;
- identité stable fondée sur le `cid`, avec préfixe propre à la source TV Emby ;
- qualité `Auto`, `1080p`, `720p` ou `540p` par environnement ;
- HLS7 non DRM résolu à la demande ;
- remux serveur H.264/AAC vers MPEG-TS avec `-c copy` ;
- fermeture idempotente et arrêt forcé de FFmpeg après cinq secondes ;
- assainissement central de toutes les lignes FFmpeg avant log.

L'EPG est présent dans les sources courantes et s'intègre à la tâche native
**Actualiser le guide** d'Emby. Replay, enregistrements hébergés par Zattoo et
Widevine restent absents. La planification et l'exécution d'un enregistrement
Emby depuis le guide ont été validées sur serveur réel.

## Construire le paquet

```powershell
dotnet8 build '.\Emby.Zattoo.sln' --configuration Release
```

Le build crée :

```text
artifacts\Emby.Zattoo\Emby.Zattoo.dll
```

Cette DLL autonome constitue le paquet manuel du MVP. Les assemblies Emby et
.NET sont fournies par Emby Server et ne doivent pas être copiées depuis NuGet
dans le dossier des plugins.

## Installer sur Linux

1. Vérifier que le serveur utilise une version Emby 4.9 compatible.
2. Arrêter Emby Server.
3. Localiser le dossier `programdata/plugins` de cette installation. Son chemin
   varie selon paquet natif, Docker et volume personnalisé.
4. Supprimer toute ancienne copie de `Emby.Zattoo.Core.dll`, puis copier
   **`Emby.Zattoo.dll`** directement dans ce dossier.
5. Vérifier qu'elle est lisible par l'utilisateur qui exécute Emby.
6. Démarrer Emby Server.
7. Vérifier dans le log serveur :

```text
Emby.Zattoo plugin loaded; DRM streams remain unsupported.
```

## Configurer depuis Emby

1. Dans le tableau de bord, ouvrir **Plugins**.
2. Sur **Zattoo Live TV**, ouvrir **Settings**.
3. Saisir le compte Zattoo, choisir la qualité et renseigner le chemin FFmpeg.
4. Conserver `https://zattoo.com/` comme Provider URL sauf compte revendeur.
5. Conserver **Enrich guide descriptions** activé pour charger les résumés et
   genres manquants en arrière-plan.
6. Enregistrer. Le tuner prendra la nouvelle configuration sans redémarrage.
7. Ouvrir **Live TV → Tuner Devices → Add** et choisir **Zattoo**.
8. Enregistrer le tuner puis actualiser les chaînes.
9. Configurer la profondeur du guide, de un à quatorze jours, puis lancer la
   tâche Emby **Actualiser le guide**.

Paramètres proposés :

```text
Zattoo username                obligatoire
Zattoo password                obligatoire
Preferred quality             Auto, 1080p, 720p ou 540p
Channel import mode           Playable channels only recommandé
FFmpeg executable             chemin Linux absolu recommandé
Provider URL                  https://zattoo.com/ par défaut
Zattoo web application version conserver la valeur proposée
```

Le mot de passe est chiffré côté serveur avant écriture. Quand la page est
rouverte, elle reçoit seulement `**********`, jamais le secret ni sa valeur
chiffrée. Modifier les paramètres pendant une lecture n'interrompt pas le flux
déjà ouvert ; la nouvelle configuration est utilisée à la demande suivante.

Le tuner peut utiliser l'option Emby `Import favorites only`. Par défaut, seules
les chaînes ayant une qualité disponible non-DRM sont importées et alimentent
l'EPG. Sa capacité simultanée suit la limite détectée pour le compte afin de
permettre, lorsque celle-ci est supérieure à un, une lecture pendant un
enregistrement. Cette capacité n'ajoute aucune gestion multi-utilisateur au
plugin.

## Valider l'EPG

Avant le test Emby, mesurer ce que le compte reçoit réellement :

```powershell
dotnet8 run --project '.\src\Zattoo.Spike\Zattoo.Spike.csproj' --configuration Release --no-build -- epg-survey 14
```

La commande affiche seulement des totaux et des durées : nombre de chaînes avec
guide, nombre de programmes, horizon maximal et nombre de chaînes approchant la
profondeur demandée. Elle n'affiche aucun cookie, jeton ou corps de réponse.

Le sondage réel sur 14 jours a retourné 214 215 programmes futurs pour 491 des
493 chaînes. Parmi elles, 490 atteignent la profondeur demandée à six heures
près. L'horizon maximal de 14,2 jours provient d'une émission qui commence avant
la limite et se termine après ; il ne signale pas un dépassement des requêtes.

Le sondage seul valide la source Zattoo et le chargement par le Core ; l'import
dans la base Emby et l'enregistrement exigent les tests serveur suivants.

Le chargement réel sur 7 jours a ensuite retourné 114 740 programmes en
27,7 secondes. La première tâche native Emby a importé ces données en
7 min 16 s et s'est terminée normalement. Cette durée inclut principalement le
traitement et l'écriture du guide dans la base Emby, pas seulement les requêtes
Zattoo. Une seconde actualisation s'est terminée en 6 min 04 s. Les logs montrent
les sauvegardes et suppressions incrémentales effectuées chaîne par chaîne par
le dépôt SQLite d'Emby.

La programmation a ensuite survécu à l'actualisation, la médiathèque
d'enregistrements a été créée, le fichier produit était lisible et le stream a
été fermé proprement. Le parcours DVR est donc validé sur le serveur réel.

L'enrichissement détaillé est volontairement asynchrone. Son cache persiste dans
le dossier de données du plugin et survit donc aux redémarrages. Une empreinte
des données de base permet de réutiliser les détails d'un programme inchangé ;
seuls les programmes nouveaux ou modifiés sont remis en file. Les réponses
encore sans description sont retentées de manière espacée et toutes les entrées
sont purgées six heures après la fin du programme.

Les lots contiennent au maximum 20 identifiants et sont espacés d'une seconde.
Les programmes courants et suivants sont traités avant les favoris, puis viennent
les prochaines 24 heures. Le guide de base reste complet sur toute la profondeur
demandée, mais un programme non favori plus éloigné attend d'entrer dans cette
fenêtre glissante avant le chargement de sa description. Ouvrir une chaîne
redonne la priorité à son programme courant et au suivant, sans bloquer la
lecture. Une nouvelle actualisation native Emby reste nécessaire pour importer
dans sa base les descriptions déjà arrivées dans le cache du plugin.

Après installation de la nouvelle DLL :

1. ouvrir les tâches planifiées du serveur Emby ;
2. exécuter **Actualiser le guide** ;
3. vérifier le message `Refreshing guide with ... days of guide data` ;
4. ouvrir le guide et contrôler plusieurs chaînes et plusieurs jours ;
5. suivre les logs `Zattoo guide detail enrichment ...` jusqu'au message de fin ;
6. relancer **Actualiser le guide** et vérifier les descriptions disponibles ;
7. programmer un enregistrement ponctuel depuis une émission future ;
8. relancer l'actualisation et vérifier que la programmation demeure associée
   à la même émission.

La présence de quatorze jours dépend de Zattoo. Le plugin accepte toute plage
transmise par Emby jusqu'à cette profondeur, mais ne fabrique pas les programmes
absents de la réponse du fournisseur.

## Validation Milestone 3

Vérifier dans cet ordre :

1. le plugin apparaît dans la liste des plugins avec une action **Settings** ;
2. la configuration s'enregistre et le mot de passe reste masqué à la réouverture ;
3. le type de tuner Zattoo est proposé ;
4. l'enregistrement du tuner charge les chaînes ;
5. `RTS 1 HD` apparaît avec le bon numéro et le bon logo ;
6. la lecture démarre dans Emby Web ;
7. l'arrêt produit `Closed Zattoo channel ...` dans le log ;
8. aucun processus `ffmpeg` enfant ne subsiste après l'arrêt ;
9. aucun password, cookie, token ou URL signée complète n'apparaît dans le log.

Le GO / NO-GO n°2 ne sera prononcé qu'après lecture Emby Web, changements de
chaîne et test longue durée.

## Limites connues

- les URLs signées sont invisibles dans les logs mais restent visibles dans la
  ligne de commande du processus FFmpeg au niveau du système d'exploitation ;
- le binaire est compilé pour la ligne stable Emby 4.9 ; toute autre version du
  serveur doit être confirmée avant installation ;
- les avertissements fMP4 `duplicated MOOV` sont comptés à la fermeture mais ne
  sont pas répétés dans les logs ;
- l'emplacement de FFmpeg dépend de l'installation Linux ; le binaire doit être
  exécutable par l'utilisateur Emby.
