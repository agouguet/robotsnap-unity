using UnityEngine;

namespace RobotSNAP.Agents
{
    [CreateAssetMenu(fileName = "HumanConfig", menuName = "RobotSNAP/Human/Config")]
    public class HumanConfig : ScriptableObject
    {
        [Header("Display Toggles (Editor only)")]
        [SerializeField] private bool showMovementParameters = true;
        [SerializeField] private bool showMovementController = true;
        [SerializeField] private bool showSFMParameters = true;
        [SerializeField] private bool showAnimationParameters = true;
        [SerializeField] private bool showNavigationParameters = true;
        [SerializeField] private bool showVisualizationParameters = true;

        // ==================== MOVEMENT ====================
        [Header("Movement")]
        [Tooltip("Vitesse désirée en conditions normales (m/s)")]
        public float desiredSpeed = 0.8f;

        [Tooltip("Vitesse maximale (m/s)")]
        public float maxSpeed = 1.2f;

        [Tooltip("Distance de ralentissement avant le but (m)")]
        public float slowDownDistance = 1.5f;

        // ==================== CONTROLLER SELECTION ====================
        [Header("Controller Selection")]
        [Tooltip("Type de contrôleur utilisé : SFM, External, Manual ou Replay")]
        public MovementControllerType controllerType = MovementControllerType.SFM;

        // ==================== SFM PARAMETERS ====================
        [Header("SFM - Agent Physical Properties")]
        [Tooltip("Masse de l'agent (kg)")]
        public float agentMass = 80f;

        [Tooltip("Rayon de l'agent (m)")]
        public float agentRadius = 0.25f;

        [Header("SFM - Goal Force")]
        [Tooltip("Temps de relaxation (inertie)")]
        public float relaxationTime = 0.5f;

        [Tooltip("Intensité de la force d'attraction vers le but")]
        public float goalForceStrength = 10f;

        [Tooltip("Distance à partir de laquelle la force d'attraction s'applique pleinement")]
        public float goalForceDistance = 5f;


        [Header("SFM - Social Forces")]
        [Tooltip("Rayon de perception des autres agents (m)")]
        public float perceptionRadiusAgent = 2.5f;

        [Tooltip("Force d'interaction sociale (A)")]
        public float socialForceA = 375f;       // 1500/4
        

        [Tooltip("Portée de l'interaction sociale (B)")]
        public float socialForceB = 0.16f;      // 0.08*2

        [Tooltip("Intensité de l'alignement avec les voisins")]
        public float alignmentStrength = 0.3f;

        [Header("SFM - Contact Forces")]
        [Tooltip("Rigidité du contact (collision)")]
        public float contactStiffnessK = 120000f;   // 1.2E5

        [Tooltip("Frottement de contact")]
        public float contactFrictionKappa = 240000f; // 2.4E5

        [Header("SFM - Wall Forces")]
        [Tooltip("Force d'interaction avec les murs (A)")]
        public float wallForceA = 2400f;        // 600*4

        [Tooltip("Portée de l'interaction avec les murs (B)")]
        public float wallForceB = 0.12f;        // 0.04*3

        [Tooltip("Rigidité du contact avec les murs")]
        public float wallContactStiffnessK = 120000f;

        [Tooltip("Frottement de contact avec les murs")]
        public float wallContactFrictionKappa = 240000f;

        [Header("SFM - Obstacle Forces")]
        [Tooltip("Rayon de perception des obstacles statiques")]
        public float obstaclePerceptionRadius = 2f;

        [Tooltip("Force de répulsion des obstacles")]
        public float obstacleForceStrength = 8f;

        [Tooltip("Portée de la force de répulsion des obstacles")]
        public float obstacleForceDistance = 0.5f;

        [Header("SFM - Robot Forces")]
        [Tooltip("Rayon de perception du robot")]
        public float robotPerceptionRadius = 3f;

        [Tooltip("Force de répulsion du robot")]
        public float robotRepulsionStrength = 10f;

        [Tooltip("Portée de la force de répulsion du robot")]
        public float robotForceDistance = 0.8f;

        [Header("SFM - Dampening")]
        [Tooltip("Amortissement du mouvement arrière")]
        public float backwardDampening = 20f;

        [Tooltip("Amortissement du mouvement latéral")]
        public float lateralDampening = 5f;

        [Tooltip("Amortissement min de répulsion des robots")]
        public float robotRepulsionDampeningMin = 0.5f;

        [Tooltip("Amortissement max de répulsion des robots")]
        public float robotRepulsionDampeningMax = 1.0f;

        // ==================== NAVIGATION ====================
        [Header("Navigation")]
        [Tooltip("Distance pour considérer le but atteint")]
        public float goalReachedDistance = 0.2f;

        [Tooltip("Intervalle de mise à jour du chemin NavMesh (secondes)")]
        public float pathUpdateInterval = 0.2f;

        [Tooltip("Distance minimale avant de passer au prochain waypoint")]
        public float nextNavMinDistance = 0.3f;

        [Tooltip("Distance pour considérer le but final atteint (fallback)")]
        public float closeEnoughMinDistance = 0.2f;

        // ==================== ANIMATION ====================
        [Header("Animation")]
        [Tooltip("Animator Controller pour l'humain")]
        public RuntimeAnimatorController animationController;

        [Tooltip("Facteur de lissage pour les animations (Forward/Strafe)")]
        public float animationSmoothing = 0.6f;

        [Tooltip("Seuil de vitesse pour considérer l'agent à l'arrêt")]
        public float idleSpeedThreshold = 0.5f;

        [Tooltip("Vitesse angulaire pour la rotation (deg/s)")]
        public float angularSpeed = 180f;

        // ==================== VISUALIZATION ====================
        [Header("Visualization")]
        public bool showPredictionGizmos = true;
        public Color predictionColor = Color.yellow;
        public Color trajectoryColor = Color.blue;

        // Validation
        private void OnValidate()
        {
            // Clamp des valeurs critiques
            desiredSpeed = Mathf.Max(0.1f, desiredSpeed);
            maxSpeed = Mathf.Max(desiredSpeed, maxSpeed);
            slowDownDistance = Mathf.Max(0.2f, slowDownDistance);
            agentMass = Mathf.Max(1f, agentMass);
            agentRadius = Mathf.Max(0.1f, agentRadius);
            relaxationTime = Mathf.Max(0.1f, relaxationTime);
            perceptionRadiusAgent = Mathf.Max(0.5f, perceptionRadiusAgent);
            obstaclePerceptionRadius = Mathf.Max(0.5f, obstaclePerceptionRadius);
            robotPerceptionRadius = Mathf.Max(0.5f, robotPerceptionRadius);
            goalReachedDistance = Mathf.Max(0.05f, goalReachedDistance);
            pathUpdateInterval = Mathf.Max(0.05f, pathUpdateInterval);
            angularSpeed = Mathf.Max(10f, angularSpeed);
        }
    }

    public enum MovementControllerType
    {
        SFM = 0,       // Social Force Model
        External = 1,  // vitesse commandée depuis l'extérieur (API Python, ROS2)
        Manual = 2,    // vitesse commandée au clavier, depuis la vue de simulation
        Replay = 3     // suit la trajectoire enregistrée d'un épisode (rejeu de scénario)
    }
}
