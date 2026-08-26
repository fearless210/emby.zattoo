# Emby MVP — installation et validation

Le plugin 0.2.4 compile contre le SDK Emby stable 4.9.5.0
et `MediaBrowser.Server.Core` 4.9.1.90. Il inclut une page de configuration
native Emby. Son chargement, sa configuration et l'import des chaînes ont été
validés sur Emby Linux. La lecture de RTS 1 HD fonctionne dans Emby Web ; les
tests d'arrêt, de changement de chaîne et de longue durée restent à effectuer.

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
- tuner `zattoo` avec un flux simultané par défaut ;
- chaînes TV, numéros, favoris et logos ;
- identité stable fondée sur le `cid`, avec préfixe propre à la source TV Emby ;
- qualité `Auto`, `1080p`, `720p` ou `540p` par environnement ;
- HLS7 non DRM résolu à la demande ;
- remux serveur H.264/AAC vers MPEG-TS avec `-c copy` ;
- fermeture idempotente et arrêt forcé de FFmpeg après cinq secondes ;
- assainissement central de toutes les lignes FFmpeg avant log.

EPG, Replay, enregistrement et Widevine restent absents.

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
5. Enregistrer. Le tuner prendra la nouvelle configuration sans redémarrage.
6. Ouvrir **Live TV → Tuner Devices → Add** et choisir **Zattoo**.
7. Enregistrer le tuner puis actualiser les chaînes.

Paramètres proposés :

```text
Zattoo username                obligatoire
Zattoo password                obligatoire
Preferred quality             Auto, 1080p, 720p ou 540p
FFmpeg executable             chemin Linux absolu recommandé
Provider URL                  https://zattoo.com/ par défaut
Zattoo web application version conserver la valeur proposée
```

Le mot de passe est chiffré côté serveur avant écriture. Quand la page est
rouverte, elle reçoit seulement `**********`, jamais le secret ni sa valeur
chiffrée. Modifier les paramètres pendant une lecture n'interrompt pas le flux
déjà ouvert ; la nouvelle configuration est utilisée à la demande suivante.

Le tuner peut utiliser l'option Emby `Import favorites only`. Sa limite initiale
est fixée à un stream simultané.

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
chaîne et test longue durée. Samsung Tizen appartient au Milestone 4.

## Limites connues

- les URLs signées sont invisibles dans les logs mais restent visibles dans la
  ligne de commande du processus FFmpeg au niveau du système d'exploitation ;
- le binaire est compilé pour la ligne stable Emby 4.9 ; toute autre version du
  serveur doit être confirmée avant installation ;
- les avertissements fMP4 `duplicated MOOV` sont comptés à la fermeture mais ne
  sont pas répétés dans les logs ;
- l'emplacement de FFmpeg dépend de l'installation Linux ; le binaire doit être
  exécutable par l'utilisateur Emby.
