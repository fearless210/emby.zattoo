# Changelog

Ce fichier recense les changements notables des versions publiées. Les commits
et les tags Git constituent l'historique de référence ; aucune date n'est
dupliquée ici.

## 1.1.1 — Corrections de fiabilité

### Corrigé

- lecture refusée par les clients annonçant un plafond de débit très élevé, avec
  le message *media not supported* : faute de débit source
  déclaré, Emby calait le transcodage sur ce plafond et l'encodeur dépassait le
  niveau H.264 accepté par l'appareil ; le plugin publie désormais le débit, la
  résolution et les codecs réels de la variante retenue ;
- désentrelacement systématique appliqué par Emby à un flux progressif, faute de
  caractéristiques de piste déclarées ;
- échec de lecture `HTTP 500` sur le point d'accès Live Stream lorsqu'Emby se
  rattachait au flux, notamment quand la détection des pistes précède un
  transcodage : les consommateurs sont désormais servis l'un après l'autre sur
  le même remux, avec une attente courte pour absorber leur recouvrement ;
- lecture des chaînes dont toutes les qualités non-DRM dépassent le plafond de
  qualité choisi : la préférence agit comme un plafond de débit et non comme une
  condition de lecture, avec repli sur la qualité disponible la plus basse ;
- import du catalogue lorsque l'endpoint des favoris échoue : les favoris
  décorent désormais les chaînes sans pouvoir empêcher leur chargement, et le
  tuner refuse le rafraîchissement plutôt que d'importer une liste vide lorsque
  l'option Emby **Import favorites only** est active ;
- processus FFmpeg résiduel lorsqu'un flux échouait après le démarrage du remux :
  l'URL locale Emby est validée avant tout démarrage et le processus est arrêté
  si l'ouverture échoue malgré tout ;
- accumulation des clients retirés à chaque sauvegarde de la configuration, qui
  conservait un `HttpClient`, le cache du guide et le cache des détails jusqu'à
  l'arrêt du serveur ;
- capacité de flux simultanés ramenée à 1 lors d'un changement de configuration,
  qui refusait un second flux légitime pendant un enregistrement jusqu'au
  rafraîchissement suivant des chaînes ;
- gel du tuner pendant l'arrêt de l'enrichissement du guide : cet arrêt, qui
  attend le worker, ne se produit plus en tenant le verrou du client.

### Optimisé

- journalisation de la cause réelle d'un échec de copie du flux, qu'Emby ne
  rapporte au client que sous la forme d'un `HTTP 500` ;
- journalisation numérotée des rattachements au flux, avec leur durée, afin de
  distinguer un problème de remux d'un problème de transcodage côté serveur.

### Tests

- lecture des caractéristiques d'une variante HLS, correspondance des codecs
  RFC 6381 et repli sur la qualité du catalogue sans playlist maître ;
- prise de relais entre consommateurs successifs, refus d'un consommateur
  concurrent après l'attente, et respect de l'annulation ;
- repli de qualité lorsqu'aucun niveau ne tient sous le plafond, et priorité
  conservée pour un niveau de résolution inconnue ;
- chargement du catalogue avec des favoris en erreur HTTP, en réponse malformée
  et en `401` répétés imposant un renouvellement de session ;
- cohérence entre la validation de l'URL locale Emby et son utilisation ;
- politique de retraite des clients : période de grâce, libération unique,
  libération sélective et vidage complet.

## 1.1.0 — Chaînes lisibles et capacités du compte

### Ajouté

- mode d'import **Playable channels only**, recommandé par défaut, conservant
  les chaînes mixtes dès qu'au moins une qualité disponible est non-DRM ;
- modes optionnels excluant seulement les chaînes DRM-only ou affichant le
  catalogue complet à des fins de diagnostic ;
- détection des capacités techniques du compte et du catalogue sans dépendre
  du nom commercial de l'abonnement : replay, limite d'enregistrements cloud,
  meilleure résolution non-DRM et capacité de flux simultanés ;
- ajustement du `TunerCount` Emby et verrou interne à la capacité détectée afin
  d'autoriser une lecture pendant un enregistrement lorsque le compte le permet.

### Optimisé

- parsing, cache et enrichissement EPG limités aux chaînes effectivement
  importées, y compris lorsque **Import favorites only** est actif ;
- exclusion des qualités DRM dans l'indicateur HD et maintien du repli vers la
  meilleure qualité non-DRM d'une chaîne mixte.

### Tests

- couverture des trois modes d'import, des chaînes mixtes, du filtrage EPG,
  des capacités de session et de la libération des emplacements de flux.

## 1.0.0 — Première version fonctionnelle

### Ajouté

- intégration de l'EPG Zattoo à la tâche native **Actualiser le guide** d'Emby ;
- prise en charge des plages de guide allant jusqu'aux 14 jours acceptés par
  Emby, découpées en fenêtres de cinq heures ;
- cache partagé de 30 minutes évitant de télécharger une même fenêtre pour
  chaque chaîne, avec mutualisation des demandes concurrentes ;
- mapping des titres, épisodes, horaires UTC, genres, images et métadonnées
  détaillées présentes dans la réponse du guide vers `ProgramInfo` ;
- commande `epg-survey` pour mesurer sans afficher de secret la profondeur de
  guide réellement publiée par Zattoo ;
- commande `epg-endpoint-survey` comparant les endpoints de guide v2 et v3 sur
  une même fenêtre à partir de compteurs anonymes, sans afficher de contenu ;
- commande `epg-details-survey` utilisant jusqu'à cinq lots et client borné à
  20 identifiants par appel pour mesurer la couverture et le coût des
  descriptions sans afficher leur contenu, avec une seconde entre les lots et
  un unique retry de transport après deux secondes ;
- enrichissement facultatif des descriptions et genres EPG en arrière-plan,
  sans attente supplémentaire dans la tâche native Emby ;
- file incrémentale par lots de 20, espacés d'une seconde, donnant la priorité
  aux programmes courants et suivants, aux chaînes favorites, aux prochaines
  24 heures, sans enrichir par avance les programmes non favoris plus éloignés ;
- fenêtre glissante limitant le temps initial, le nombre d'appels et la taille
  du cache, tout en conservant le guide de base complet jusqu'à 14 jours ;
- priorité immédiate au programme courant et au suivant lorsqu'une chaîne est
  ouverte, sans ajouter d'attente au démarrage du stream ;
- cache persistant des réponses positives et incomplètes, restauré après un
  redémarrage et isolé par un hash du compte, du fournisseur et de la langue ;
- empreinte stable de chaque programme permettant d'ignorer les données
  inchangées et de ne charger que les programmes nouveaux ou modifiés ;
- nouvelle tentative différée pour les réponses encore sans description,
  déduplication des requêtes en vol et purge six heures après la fin du
  programme ;
- journal JSON ajouté par lots et compacté périodiquement, sans dépendance
  native supplémentaire afin de conserver le déploiement avec une seule DLL ;
- alimentation de la file une seule fois lors du chargement initial de chaque
  fenêtre, sans reparcourir les programmes de toutes les chaînes à chaque appel
  d'Emby ;
- option native **Enrich guide descriptions** et logs de progression limités à
  des compteurs anonymes.

### Tests

- tests des deux formes de réponse de guide observées, du filtrage temporel,
  du renouvellement de session, du cache, de la concurrence et d'une plage
  complète de 14 jours ;
- tests du mapping vers le contrat EPG d'Emby et de la stabilité de `ShowId` ;
- tests du parsing, de la déduplication, de la limite de lot et du renouvellement
  de session pour les détails de programmes ;
- tests du traitement en arrière-plan, de la priorité, de l'enrichissement, du
  cache incomplet, de la purge, de l'ajout incrémental et de l'arrêt propre ;
- tests de restauration du cache après redémarrage, de non-rechargement d'un
  programme inchangé, d'invalidation d'un programme modifié, d'isolation entre
  comptes et de priorité déclenchée par l'ouverture d'une chaîne ;
- test excluant de la file les programmes non favoris situés au-delà de la
  fenêtre glissante de 24 heures ;
- sondage réel sur 14 jours : 214 215 programmes futurs, 491 chaînes couvertes
  sur 493 et 490 chaînes atteignant la cible à six heures près ;
- chargement réel de 114 740 programmes sur 7 jours par le Core en 27,7 secondes,
  puis imports complets par la tâche native Emby en 7 min 16 s et 6 min 04 s ;
- planification persistante et exécution réelle d'un enregistrement, avec
  création de la médiathèque, fichier lisible et fermeture propre du stream ;
- premier sondage réel de 100 identifiants en un appel : 20 détails retournés,
  dont 17 descriptions et 14 genres, confirmant la nécessité de lots plus
  petits ;
- second sondage réel en cinq lots espacés : 100 détails retournés sur 100 sans
  retry en 4,1 s, dont 88 descriptions et 73 genres ;
- comparaison réelle sur une fenêtre de cinq heures : les endpoints v2 et v3
  ont retourné les mêmes 3 840 programmes et les mêmes métadonnées exploitables,
  sans description directe ; l'endpoint v3 reste donc le chemin retenu ;
- validation réelle de l'enrichissement dans Emby : descriptions visibles,
  cache persistant restauré après redémarrage et seulement 213 détails traités
  après limitation de la file, au lieu de plus de 99 000 ;
- actualisation native finale du guide terminée en 8 min 18 s avec programmes et
  descriptions disponibles dans l'interface Emby.

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
