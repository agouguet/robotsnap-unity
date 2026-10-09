using UnityEngine;

namespace RobotSNAP.Agents
{
    /// <summary>
    /// Conduit un robot qui n'a pas de roues en déplaçant sa base de façon cinématique.
    ///
    /// Un humanoïde comme Ginger porte une cinquantaine d'articulations : lui commander des vitesses de roues
    /// n'a aucun sens, et lui laisser la gravité ferait tomber un corps que personne ne sait équilibrer. On
    /// pose donc la base à la main, pas à pas. Le résultat est stable et identique d'une machine à l'autre,
    /// ce qui compte pour comparer deux exécutions d'un même scénario.
    /// </summary>
    public class KinematicBaseDrive : MonoBehaviour, IRobotDrive
    {
        /// <summary>
        /// Au-delà de ce délai sans consigne, le robot est arrêté. C'est la même règle que RobotInputController :
        /// un client qui se tait ne laisse pas le robot filer indéfiniment.
        /// </summary>
        private const float CommandTimeoutSeconds = 0.5f;

        [Tooltip("Base articulée du robot. Vide : cherchée automatiquement.")]
        [SerializeField] private ArticulationBody _baseLink;
        [SerializeField] private bool _logWarnings = false;

        // Dernière consigne reçue, et l'instant où elle est arrivée. Sans nouvelle consigne, elles expirent.
        private float _targetLinearSpeed;
        private float _targetAngularSpeed;
        private float _lastCommandTime = float.NegativeInfinity;

        // La pose que ce composant a écrite en dernier. On intègre à partir d'elle et jamais à partir du
        // transform : TeleportRoot n'est reporté sur le transform qu'au pas physique suivant, si bien que
        // relire le transform juste après un téléport ramènerait le robot à l'endroit d'où on vient de le
        // sortir - ce qui arrivait, et faisait apparaître chaque robot cinématique à l'origine de la scène.
        private Vector3 _pose;
        private float _yaw;
        private bool _poseKnown;

        // TeleportRoot est autoritaire : l'appeler deux fois dans le même pas physique appliquerait deux fois
        // la même commande et doublerait la vitesse. On mémorise donc le pas déjà intégré.
        private float _lastIntegrationFixedTime = float.NegativeInfinity;

        /// <summary>Consigne de vitesse linéaire courante (m/s), pour l'interface et le débogage.</summary>
        public float CurrentLinearSpeed => _targetLinearSpeed;

        /// <summary>Consigne de vitesse angulaire courante (rad/s), pour l'interface et le débogage.</summary>
        public float CurrentAngularSpeed => _targetAngularSpeed;

        private void Awake()
        {
            if (_baseLink == null)
            {
                _baseLink = FindBaseLink();
            }

            if (_baseLink == null)
            {
                Debug.LogError(
                    "KinematicBaseDrive: aucune ArticulationBody trouvée pour '" + name +
                    "'. Le robot ne peut pas être déplacé ; on désactive le composant au lieu de le laisser " +
                    "immobile en silence.", this);
                enabled = false;
                return;
            }

            // La base est posée et orientée par le poseur de scénario, et c'est nous qui la déplaçons ensuite.
            // Rendre la base immobile annule la gravité et les réactions : le robot reste debout, à la pose
            // exacte où il a été placé, sans qu'aucune articulation n'ait à tenir le corps.
            _baseLink.immovable = true;
            _baseLink.useGravity = false;
        }

        /// <summary>
        /// Vitesse linéaire (m/s) et angulaire (rad/s) voulues, dans le repère du robot.
        /// La consigne est seulement mémorisée ici : le déplacement se fait dans FixedUpdate.
        /// </summary>
        public void SetRobotVelocity(float linearSpeed, float angularSpeed)
        {
            _targetLinearSpeed = linearSpeed;
            _targetAngularSpeed = angularSpeed;
            _lastCommandTime = Time.time;

            if (_logWarnings && _baseLink == null)
            {
                Debug.LogWarning(
                    "KinematicBaseDrive: consigne ignorée pour '" + name + "', aucune base articulée trouvée.",
                    this);
            }
        }

        /// <summary>
        /// Pose la base. Appelé par le robot qui vient de téléporter son châssis : la pose est mémorisée ici
        /// pour que l'intégration reparte de là au lieu de repartir de la position précédente.
        /// </summary>
        public void SetBasePose(Vector3 position, Quaternion rotation)
        {
            _pose = position;
            _yaw = rotation.eulerAngles.y;
            _poseKnown = true;

            if (_baseLink != null)
            {
                _baseLink.TeleportRoot(position, Quaternion.Euler(0f, _yaw, 0f));
                _baseLink.WakeUp();
            }
        }

        private void FixedUpdate()
        {
            if (_baseLink == null) return;

            // Un seul déplacement par pas physique, quelle que soit la façon dont FixedUpdate est appelé.
            if (Mathf.Approximately(_lastIntegrationFixedTime, Time.fixedTime)) return;
            _lastIntegrationFixedTime = Time.fixedTime;

            // Un châssis que personne n'a posé - un robot ajouté à la main dans la scène - démarre là où le
            // transform le dit, et non à l'origine.
            if (!_poseKnown)
            {
                _pose = _baseLink.transform.position;
                _yaw = _baseLink.transform.eulerAngles.y;
                _poseKnown = true;
            }

            float linearSpeed = _targetLinearSpeed;
            float angularSpeed = _targetAngularSpeed;
            if (Time.time - _lastCommandTime > CommandTimeoutSeconds)
            {
                linearSpeed = 0f;
                angularSpeed = 0f;
            }

            float dt = Time.fixedDeltaTime;

            // Déplacement planaire : on avance le long de l'avant du robot, et l'Y de la pose est conservé
            // tel quel. Le robot reste donc sur le sol, sans dérive verticale ni flottement.
            Vector3 forward = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
            _pose += forward * (linearSpeed * dt);
            // La consigne angulaire est en convention ROS : positive vers la gauche. Unity tourne dans
            // l'autre sens autour de son axe vertical - un lacet qui augmente est une rotation horaire
            // vue de dessus - donc avancer le lacet Unity demande de soustraire la consigne. Ajouter
            // faisait tourner ce robot a l'oppose de ses roues, et de l'inverse de ce que le planificateur
            // avait demande.
            _yaw -= angularSpeed * Mathf.Rad2Deg * dt;

            _baseLink.TeleportRoot(_pose, Quaternion.Euler(0f, _yaw, 0f));

            // TeleportRoot laisse le corps endormi : on le réveille pour que le rendu et les capteurs suivent
            // la nouvelle pose dès ce pas.
            _baseLink.WakeUp();
        }

        /// <summary>
        /// Remet la consigne à zéro. La pose n'est pas touchée : après un téléport, c'est le poseur de scénario
        /// qui sait où le robot doit se trouver, pas ce composant.
        /// </summary>
        public void ResetDrives()
        {
            _targetLinearSpeed = 0f;
            _targetAngularSpeed = 0f;
            _lastCommandTime = float.NegativeInfinity;
        }

        /// <summary>
        /// Première base articulée de l'arbre : on remonte les parents tant qu'ils portent une ArticulationBody,
        /// puis on cherche la base dans le GameObject qui coiffe l'arbre. C'est ce GameObject qui est
        /// déplaçable d'un bloc ; déplacer un lien intermédiaire ne déplacerait qu'une partie du robot.
        /// </summary>
        private ArticulationBody FindBaseLink()
        {
            Transform current = transform;
            while (current != null && current.GetComponent<ArticulationBody>() != null && current.parent != null)
            {
                current = current.parent;
            }

            return current != null ? current.GetComponentInChildren<ArticulationBody>() : null;
        }
    }
}
