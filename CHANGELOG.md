# Changelog

Ce fichier recense les changements notables des versions publiées. Les commits
et les tags Git constituent l'historique de référence ; aucune date n'est
dupliquée ici.

## 0.2.4

### Corrigé

- préfixage des identifiants de chaînes avec l'identifiant de la source TV Emby,
  tout en conservant le `cid` Zattoo brut dans `TunerChannelId` ;
- routage de la lecture vers la bonne instance de `ZattooTunerHost`, supprimant
  l'erreur `Tuner not found` ;
- exposition du remux MPEG-TS ouvert par le plugin via le point d'accès local
  `/LiveTv/LiveStreamFiles/<id>/stream.ts` ;
- remplacement du chemin virtuel `zattoo://<cid>` avant la lecture par FFmpeg,
  supprimant l'erreur `Protocol not found`.

### Tests et validation

- tests de non-régression sur l'identité Emby/Zattoo des chaînes ;
- tests du basculement de la source virtuelle vers le point d'accès HTTP local ;
- chargement, configuration, catalogue et lecture de RTS 1 HD validés sur Emby
  Server 4.9.5.0 sous Linux ;
- aucune URL Zattoo signée, aucun cookie et aucun identifiant de compte transmis
  au client Emby.

### Mise à niveau

- depuis `0.2.2`, la source TV Zattoo doit être supprimée puis recréée une fois
  afin qu'Emby réimporte les chaînes avec leurs nouveaux identifiants ;
- la mise à niveau depuis une DLL de test `0.2.3` ne nécessite pas de recréer la
  source si cette réimportation a déjà été effectuée.

## 0.2.2

### Corrigé

- résolution correcte sous Linux des chemins relatifs fournis par les métadonnées
  de l'application Zattoo ;
- conservation des URL HTTPS de logos sans conversion accidentelle en URL
  `file://`.

## 0.2.1

### Corrigé

- intégration du Core Zattoo directement dans `Emby.Zattoo.dll` afin que le
  plugin puisse être installé avec une seule DLL ;
- suppression de la dépendance d'exécution séparée `Emby.Zattoo.Core.dll`.

## 0.2.0

### Ajouté

- première préversion publique du plugin Live TV ;
- authentification Zattoo, import des chaînes, favoris et logos ;
- sélection des flux HLS non-DRM et remux MPEG-TS côté serveur ;
- page de configuration Emby, tests automatisés et publication des assets par
  GitHub Actions.
