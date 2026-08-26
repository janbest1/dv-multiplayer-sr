using DV.Interaction;
using DV.Player;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Editor.Components.Player;
using Multiplayer.Networking.Data.Player;
using System.Collections.Generic;
using UnityChan;
using UnityEngine;

namespace Multiplayer.Components.Networking.Player;

/// <summary>
/// Represents a networked player in the multiplayer environment, handling movement, item holding, and visual state
/// </summary>
public class NetworkedPlayer : MonoBehaviour
{
    #region Static Setup

    //Where the game holds an item, measured in the camera's own frame, and how high that camera
    //sits above the player. Both are read off the local player once and then used to place every
    //other player's items, so neither may depend on where this player happened to be looking.
    private static Vector3 itemAnchorFromCamera = new(0.2f, -0.1f, 0.4f);
    private static float itemAnchorCameraHeight = 1.6f;

    /// <summary>
    /// Learns from the local player where a held item belongs, so the same pose can be given to
    /// every other player's hands.
    ///
    /// The anchor rides in front of the camera, so that is where it has to be measured. Measuring
    /// it against the player instead folded the look direction into the answer: two machines in the
    /// same session came out 30cm apart in height and half a metre in depth, and each of them then
    /// drew the other player's items at its own idea of where a hand is.
    /// </summary>
    public static void CaptureItemAnchorOffset()
    {
        if (VRManager.IsVREnabled())
            return;

        Transform player = PlayerManager.PlayerTransform;
        Camera camera = PlayerManager.ActiveCamera;
        ItemPositionController controller = ItemPositionController.Instance;

        if (player == null || camera == null || controller == null || controller.itemAnchor == null)
        {
            Multiplayer.LogWarning("NetworkedPlayer.CaptureItemAnchorOffset() Nothing to measure against, keeping the defaults");
            return;
        }

        itemAnchorFromCamera = camera.transform.InverseTransformPoint(controller.itemAnchor.position);
        itemAnchorCameraHeight = player.InverseTransformPoint(camera.transform.position).y;

        Multiplayer.LogDebug(() => $"NetworkedPlayer.CaptureItemAnchorOffset() anchor from camera: {itemAnchorFromCamera}, camera height: {itemAnchorCameraHeight}");
    }
    #endregion

    private const float LERP_SPEED = 5.0f;
    private const float MAX_LEAN_ANGLE = 50f;
    private const float LEAN_SMOOTHING_DURATION = 0.1f;
    private const float HEAD_LEAN_MULTIPLIER = 1.5f;

    public byte PlayerId { get; set; }
    public string CrewName { get; set; }
    public bool IsVR { get; set; }

    private GameObject playerModel;
    private AnimationHandler animationHandler;
    private NameTag nameTag;
    private int ping;
    private NetworkedPlayerIKHandler ikHandler;

    private string username;

    public string Username
    {
        get => username;
        set
        {
            username = value;
            nameTag?.SetUsername(value);
        }
    }

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrEmpty(CrewName))
                return username;
            return $"[{CrewName}] {username}";
        }
    }

    // World Positioning
    internal bool IsOnCar { get; private set; }
    internal NetworkedTrainCar OccupiedCar { get; private set; }

    private Transform selfTransform;
    private PlayerPostureFlags currentPosture;

    // Head tracking
    private Transform headTransform;
    private Quaternion headBaseWorldRotation = Quaternion.identity;
    private float currentHeadPitch;
    private float targetHeadPitch;

    // Spine tracking
    private Transform spineTransform;
    private Quaternion spineBaseWorldRotation = Quaternion.identity;

    // Player movement and rotation
    private Vector3 targetPos;
    private Quaternion targetRotation;
    private Vector2 moveDir;
    private Vector2 targetMoveDir;

    private float currentLeanAngle;
    private float angleSmoothRefVel;
    private float currentSitHeight;

    // VR hand tracking — targets set from incoming packets
    private Transform leftHandTransform;
    private Transform rightHandTransform;
    private Vector3 targetLeftHandPos;
    private Quaternion targetLeftHandRot = Quaternion.identity;
    private Vector3 targetRightHandPos;
    private Quaternion targetRightHandRot = Quaternion.identity;

    // Current lerped values — tracked independently to avoid Animator fighting
    private Vector3 currentLeftHandWorldPos;
    private Quaternion currentLeftHandWorldRot = Quaternion.identity;
    private Vector3 currentRightHandWorldPos;
    private Quaternion currentRightHandWorldRot = Quaternion.identity;
    private bool handTrackingInitialized;

    // Inventory and item holding
    private GameObject inventoryRoot;  // created on demand, see EnsureInventoryRoot()
    public GameObject RightHandItemGO { get; private set; }
    private readonly List<Collider> disabledRHItemColliders = [];
    public GameObject LeftHandItemGO { get; private set; }
    private readonly List<Collider> disabledLHItemColliders = [];
    private Vector3? itemHoldPos;
    private Quaternion? itemHoldRot;

    WindPhysicsController windController;

    private bool isCulled;
    public bool IsCulled
    {
        get => isCulled;
        set
        {
            if (isCulled == value)
                return;

            isCulled = value;

            playerModel?.SetActive(!value);
            nameTag?.gameObject.SetActive(!value);

            //A held item is not part of the model, it would be left hanging in mid-air
            if (RightHandItemGO != null)
                RightHandItemGO.SetActive(!value);
        }
    }

    protected void Awake()
    {
        nameTag = GetComponentInChildren<NameTag>();

        nameTag.LookTarget = PlayerManager.ActiveCamera.transform;
        PlayerManager.CameraChanged += () => nameTag.LookTarget = PlayerManager.ActiveCamera.transform;

        if (name != null)
            nameTag.SetUsername(name);

        OnSettingsUpdated(Multiplayer.Settings);
        Settings.OnSettingsUpdated += OnSettingsUpdated;

        selfTransform = transform;
        targetPos = selfTransform.position;
        targetRotation = selfTransform.rotation;

        targetHeadPitch = 0f;

        moveDir = Vector2.zero;
        targetMoveDir = Vector2.zero;

        currentPosture = PlayerPostureFlags.None;

        var clampedSitHeight = Mathf.Clamp
            (
                CustomFirstPersonController.PLAYER_SITTING_HEIGHT,
                CustomFirstPersonController.MIN_PLAYER_SITTING_HEIGHT,
                CustomFirstPersonController.MAX_PLAYER_SITTING_HEIGHT
            );
        currentSitHeight = Mathf.InverseLerp
            (
                CustomFirstPersonController.MIN_PLAYER_SITTING_HEIGHT,
                CustomFirstPersonController.MAX_PLAYER_SITTING_HEIGHT,
                clampedSitHeight
            );
    }

    protected void OnDestroy()
    {
        Settings.OnSettingsUpdated -= OnSettingsUpdated;

        //A held item is parented to us and would be destroyed along with us
        if (!UnloadWatcher.isQuitting && !UnloadWatcher.isUnloading && RightHandItemGO != null)
            DropItem();
    }

    private void OnSettingsUpdated(Settings settings)
    {
        nameTag.ShowUsername(settings.ShowNameTags);
        nameTag.ShowPing(settings.ShowNameTags && settings.ShowPingInNameTags);
    }

    public void ChangeModel(GameObject newModel)
    {
        if (newModel == playerModel || newModel == null)
            return;

        if (playerModel != null)
        {
            animationHandler = null;
            DestroyImmediate(playerModel);
            headTransform = null;
            leftHandTransform = null;
            rightHandTransform = null;
            handTrackingInitialized = false;
            windController = null;
        }

        playerModel = Instantiate(newModel, transform);
        animationHandler = playerModel.GetComponent<AnimationHandler>();

        // If the model is using wind physics, e.g. for hair, add the WindPhysicsController to manage effects on and off the car
        if (playerModel.GetComponentInChildren<SpringManager>(true) != null)
            windController = playerModel.AddComponent<WindPhysicsController>();

        var animator = playerModel.GetComponentInChildren<Animator>(true);
        if (animator != null)
        {
            if (IsVR)
            {
                // Track VR Networked player's IK state for hands and feet
                ikHandler = animator.gameObject.AddComponent<NetworkedPlayerIKHandler>();
                ikHandler.IsActive = false;
            }

            headTransform = animator.GetBoneTransform(HumanBodyBones.Head);
            if (headTransform == null)
                Multiplayer.LogWarning($"Head bone not found in model {newModel.name}. Head tracking will not work");

            spineTransform = animator.GetBoneTransform(HumanBodyBones.Spine);

            leftHandTransform = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            rightHandTransform = animator.GetBoneTransform(HumanBodyBones.RightHand);

            if (leftHandTransform == null || rightHandTransform == null)
                Multiplayer.LogWarning($"Hand bones not found in model {newModel.name}. VR hand tracking will not work");
        }
        else
        {
            Multiplayer.LogWarning($"Animator not found in model {newModel.name}. Tracking will not work");
        }

        if (spineTransform == null)
        {
            // Fall back to using the model's transform if the spine bone is not found
            spineTransform = playerModel.transform;
        }

        spineBaseWorldRotation = Quaternion.Inverse(selfTransform.rotation) * spineTransform.rotation;

        if (headTransform != null)
            headBaseWorldRotation = Quaternion.Inverse(selfTransform.rotation) * headTransform.rotation;

        SetPosture(currentPosture);

        if (IsCulled)
            playerModel.SetActive(false);
    }

    public void SetPing(int ping)
    {
        nameTag?.SetPing(ping);
        this.ping = ping;
    }

    public int GetPing()
    {
        return ping;
    }

    //Beyond this a change of position is a jump, not a step
    private const float TELEPORT_SQR_DISTANCE = 25f * 25f;

    //How far away a player has to be before we stop drawing them, and how far back in before we
    //start again. The sweep that applies these lives in ClientPlayerManager.
    public const float CULL_SQR_DISTANCE = 150f * 150f;
    public const float ACTIVATE_SQR_DISTANCE = 145f * 145f;

    protected void Update()
    {
        if (IsCulled)
        {
            if (IsOnCar)
                selfTransform.localPosition = targetPos;
            else
                selfTransform.position = targetPos + WorldMover.currentMove;

            selfTransform.rotation = targetRotation;
            RefreshHeldItemPose();
            return;
        }

        float t = Time.deltaTime * LERP_SPEED;

        Vector3 from = IsOnCar ? selfTransform.localPosition : selfTransform.position;
        Vector3 to = IsOnCar ? targetPos : targetPos + WorldMover.currentMove;

        //Smoothing is for walking. A player who used the map to travel is somewhere else entirely,
        //and easing them there sends them gliding across the world - with whatever they are
        //carrying in tow, far enough from us for the game to decide the item has been lost.
        bool jumped = (to - from).sqrMagnitude > TELEPORT_SQR_DISTANCE;
        Vector3 position = jumped ? to : Vector3.Lerp(from, to, t);

        // Calculate smoothed head pitch for use in VR and nonVR head positioning and nonVR item positioning
        currentHeadPitch = Mathf.Lerp(currentHeadPitch, targetHeadPitch, t);

        moveDir = Vector2.Lerp(moveDir, targetMoveDir, t);
        animationHandler?.SetMoveDir(moveDir);

        if (!IsVR)
            animationHandler?.SetSitHeight(currentSitHeight);

        if (IsOnCar && OccupiedCar != null)
        {
            selfTransform.localPosition = position;

            // Calculate a world-up-respecting rotation
            // This creates a rotation where Y points up in world space
            // but the forward direction aligns with the car's forward projected onto the horizontal plane
            Vector3 carForward = OccupiedCar.transform.forward;
            Vector3 worldUp = Vector3.up;

            // Project car's forward onto the horizontal plane
            Vector3 horizontalForward = Vector3.ProjectOnPlane(carForward, worldUp).normalized;
            if (horizontalForward.sqrMagnitude < 0.001f)
                horizontalForward = Vector3.ProjectOnPlane(OccupiedCar.transform.right, worldUp).normalized;

            // Create base orientation aligned with world up but facing car's forward direction
            Quaternion baseRotation = Quaternion.LookRotation(horizontalForward, worldUp);

            // Calculate the relative rotation: how much is the player rotated relative to the car?
            float carYaw = baseRotation.eulerAngles.y;
            float playerYaw = targetRotation.eulerAngles.y;
            float relativeYaw = playerYaw - carYaw;

            // Apply the desired Y rotation (player's facing direction) on top of this base rotation
            Quaternion targetWorldRotation = baseRotation * Quaternion.Euler(0, relativeYaw, 0);

            // Apply rotation in world space despite being a child transform
            selfTransform.rotation = Quaternion.Lerp(selfTransform.rotation, targetWorldRotation, t);
        }
        else
        {
            selfTransform.position = position;
            selfTransform.rotation = Quaternion.Lerp(transform.rotation, targetRotation, t);
        }

        if (jumped)
            RecheckCulling();

        RefreshHeldItemPose();
    }

    /// <summary>
    /// Decides there and then whether this player is still close enough to draw. The regular sweep
    /// only runs every couple of seconds, which is fine for someone walking away and far too slow
    /// for someone who jumped: for those seconds they stand in full view on the far side of the
    /// world, and the game is left to conclude that whatever they are carrying has been lost.
    /// </summary>
    private void RecheckCulling()
    {
        Transform localPlayer = PlayerManager.PlayerTransform;

        if (localPlayer == null)
            return;

        float sqrDistance = (selfTransform.position - localPlayer.position).sqrMagnitude;

        if (IsCulled ? sqrDistance < ACTIVATE_SQR_DISTANCE : sqrDistance > CULL_SQR_DISTANCE)
            IsCulled = !IsCulled;
    }

    /// <summary>
    /// Places the held item in front of the player. The item is parented to the player, but its
    /// pose is driven from here rather than from the parent so it can be offset by the item's own
    /// grab anchor.
    /// </summary>
    private void RefreshHeldItemPose()
    {
        if (RightHandItemGO == null)
            return;

        //An item is held in front of the eyes, so it follows where the player is looking - and the
        //grab anchor the item carries is written in that same frame, which is why it has to be
        //applied there rather than against the player's feet.
        Quaternion look = selfTransform.rotation * Quaternion.Euler(currentHeadPitch, 0f, 0f);
        Vector3 eye = selfTransform.position + selfTransform.up * itemAnchorCameraHeight;

        RightHandItemGO.transform.position = eye + look * (itemAnchorFromCamera + (itemHoldPos ?? Vector3.zero));
        RightHandItemGO.transform.rotation = look * (itemHoldRot ?? Quaternion.identity);
    }

    /// <summary>
    /// LateUpdate is called after animators have updated, allowing us to apply our own transformations on top of the animated posture.
    /// </summary>
    protected void LateUpdate()
    {
        if (IsCulled)
            return;

        if (!IsVR)
        {
            float targetLeanAngle = 0f;
            if (currentPosture.HasFlag(PlayerPostureFlags.LeanLeft))
                targetLeanAngle = MAX_LEAN_ANGLE;
            else if (currentPosture.HasFlag(PlayerPostureFlags.LeanRight))
                targetLeanAngle = -MAX_LEAN_ANGLE;

            currentLeanAngle = Mathf.SmoothDamp(currentLeanAngle, targetLeanAngle, ref angleSmoothRefVel, LEAN_SMOOTHING_DURATION);
        }

        ApplySpineAndHeadRotation();

        if (IsVR)
            ApplyHandTracking();
    }

    private void ApplySpineAndHeadRotation()
    {
        if (spineTransform != null)
        {
            // Reconstruct the base animated posture for this model in world space
            Quaternion currentModelSpineBase = selfTransform.rotation * spineBaseWorldRotation;

            // Define standard look/lean vectors using the main uniform player root
            // Side lean is always spinning around the root's global FORWARD axis
            Quaternion leanOffset = Quaternion.AngleAxis(currentLeanAngle, selfTransform.forward);

            // Directly assign the uniform world rotation 
            spineTransform.rotation = leanOffset * currentModelSpineBase;
        }

        if (headTransform == null)
            return;

        Quaternion currentModelHeadBase = selfTransform.rotation * headBaseWorldRotation;
        Quaternion pitchRotation = Quaternion.AngleAxis(currentHeadPitch, selfTransform.right);
        Quaternion leanTiltRotation = Quaternion.AngleAxis(currentLeanAngle * HEAD_LEAN_MULTIPLIER, selfTransform.forward);
        headTransform.rotation = pitchRotation * leanTiltRotation * currentModelHeadBase;
    }

    private void ApplyHandTracking()
    {
        if (!handTrackingInitialized || ikHandler == null)
            return;

        float t = Time.deltaTime * LERP_SPEED;

        currentLeftHandWorldPos = Vector3.Lerp(currentLeftHandWorldPos, targetLeftHandPos, t);
        currentLeftHandWorldRot = Quaternion.Lerp(currentLeftHandWorldRot, targetLeftHandRot, t);

        currentRightHandWorldPos = Vector3.Lerp(currentRightHandWorldPos, targetRightHandPos, t);
        currentRightHandWorldRot = Quaternion.Lerp(currentRightHandWorldRot, targetRightHandRot, t);

        ikHandler.LeftHandPosition = selfTransform.position + targetRotation * currentLeftHandWorldPos;
        ikHandler.LeftHandRotation = targetRotation * currentLeftHandWorldRot;
        ikHandler.RightHandPosition = selfTransform.position + targetRotation * currentRightHandWorldPos;
        ikHandler.RightHandRotation = targetRotation * currentRightHandWorldRot;
    }

    /// <summary>
    /// Feed networked tracking data into the NetworkedPlayer to update its position, rotation, and posture.
    /// </summary>
    /// <param name="trackingData"></param>
    /// <param name="posture"></param>
    /// <param name="movePacketIsOnCar"></param>
    public void UpdatePosition(PlayerTrackingData trackingData, PlayerPostureFlags posture, bool movePacketIsOnCar)
    {
        if (trackingData.Position.HasValue)
            targetPos = trackingData.Position.Value;

        if (trackingData.MoveDirection.HasValue)
        {
            targetMoveDir = trackingData.MoveDirection.Value;
        }

        if (trackingData.SitHeight.HasValue)
            currentSitHeight = Mathf.Clamp01(trackingData.SitHeight.Value);

        SetPosture(posture);

        if (IsOnCar != movePacketIsOnCar)
            return;

        if (trackingData.RotationY.HasValue)
            targetRotation = Quaternion.Euler(0, trackingData.RotationY.Value, 0);

        if (trackingData.LookPosition.HasValue)
            targetHeadPitch = trackingData.LookPosition.Value;

        if (trackingData.LeftHandPosition.HasValue)
            targetLeftHandPos = trackingData.LeftHandPosition.Value;
        if (trackingData.LeftHandRotation.HasValue)
            targetLeftHandRot = trackingData.LeftHandRotation.Value;
        if (trackingData.RightHandPosition.HasValue)
            targetRightHandPos = trackingData.RightHandPosition.Value;
        if (trackingData.RightHandRotation.HasValue)
            targetRightHandRot = trackingData.RightHandRotation.Value;

        // Todo: improve sync, the arms can be a little spaghetti-y
        if (!handTrackingInitialized)
        {
            currentLeftHandWorldPos = targetLeftHandPos;
            currentLeftHandWorldRot = targetLeftHandRot;
            currentRightHandWorldPos = targetRightHandPos;
            currentRightHandWorldRot = targetRightHandRot;
            handTrackingInitialized = true;

            if (ikHandler != null)
                ikHandler.IsActive = true;
        }
    }

    private void SetPosture(PlayerPostureFlags posture)
    {
        currentPosture = posture;
        // Swimming overrides other postures
        bool isSwimming = posture.HasFlag(PlayerPostureFlags.Swim);
        animationHandler?.SetIsSwimming(isSwimming);
        if (isSwimming)
        {
            animationHandler?.SetIsCrouching(false);
            animationHandler?.SetIsSitting(false);
            animationHandler?.SetIsJumping(false);
        }
        else
        {
            animationHandler?.SetIsJumping(posture.HasFlag(PlayerPostureFlags.Jump));
            animationHandler?.SetIsCrouching(posture.HasFlag(PlayerPostureFlags.Crouch));
            animationHandler?.SetIsSitting(posture.HasFlag(PlayerPostureFlags.Sit));
        }
    }

    public void UpdateCar(ushort netId)
    {
        bool willBeOnCar = NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar newTrainCar);

        if (OccupiedCar != null)
        {
            if (OccupiedCar == newTrainCar)
                return;

            OccupiedCar.Client_RemovePlayer(this);
            windController?.SetOnCar(null);
        }

        IsOnCar = willBeOnCar && newTrainCar != null;

        if (IsOnCar)
        {
            OccupiedCar = newTrainCar;
            selfTransform.SetParent(OccupiedCar.transform, true);
            OccupiedCar.Client_PlayerOnCar(this);
            windController?.SetOnCar(newTrainCar.TrainCar);
        }
        else
        {
            OccupiedCar = null;
            selfTransform.SetParent(null, true);
        }
    }

    /// <summary>
    /// Attach item to player object when in inventory
    /// </summary>
    /// <param name="itemGo">The item GameObject to attach</param>
    public void AddItemToInventory(GameObject itemGo)
    {
        itemGo.transform.SetParent(EnsureInventoryRoot().transform, true);
        itemGo.SetActive(false);
    }

    /// <summary>
    /// Returns the container stowed items are parented to, creating it on first use
    /// </summary>
    private GameObject EnsureInventoryRoot()
    {
        if (inventoryRoot == null)
        {
            inventoryRoot = new GameObject("InventoryRoot");
            inventoryRoot.transform.SetParent(selfTransform, false);
            inventoryRoot.SetActive(false);
        }

        return inventoryRoot;
    }

    /// <summary>
    /// Sets the player's currently held item with optional position and rotation offsets
    /// </summary>
    /// <param name="itemGo">The item GameObject to hold</param>
    /// <param name="targetPos">Optional local position offset</param>
    /// <param name="targetRot">Optional local rotation offset</param>
    /// <param name="rightHand">Indicates if the item is held in the right hand. Always true for nonVR</param>

    // TODO: This currently only supports right hand holding and will need to be expanded to support left hand items and dual hand items
    public void HoldItem(GameObject itemGo, Vector3? targetPos = null, Quaternion? targetRot = null, bool rightHand = true)
    {
        Multiplayer.LogDebug(() => $"NetworkedPlayer.HoldItem({itemGo.GetPath()}) Player: {username}, Before position: {itemGo.transform.localPosition}, rotation:  {itemGo.transform.localRotation}, Target pos: {targetPos}, Target rot: {targetRot}");

        itemGo.transform.SetParent(selfTransform, true);
        var itemGrabHandler = itemGo.GetComponentInChildren<GrabHandlerItem>();
        if (itemGrabHandler != null)
        {
            itemGrabHandler.TogglePhysics(false);
            itemGrabHandler.interactionAllowed = false;
        }

        // Disable colliders to stop annoying noises and other potential issues
        disabledRHItemColliders.Clear();
        foreach (Collider col in itemGo.GetComponentsInChildren<Collider>(true))
        {
            //Only the ones we switch off ourselves - putting the item down must not turn on a
            //collider the item had disabled for its own reasons
            if (col == null || !col.enabled)
                continue;

            Multiplayer.LogDebug(() => $"NetworkedPlayer.HoldItem() Collider: {col.name}, Type: {col.GetType()}");
            col.enabled = false;
            disabledRHItemColliders.Add(col);
        }

        RightHandItemGO = itemGo;
        itemHoldPos = targetPos;
        itemHoldRot = targetRot;

        //A culled player shows no model, so their item must not hang there on its own
        if (isCulled)
            itemGo.SetActive(false);

        RefreshHeldItemPose();
    }

    /// <summary>
    /// Drops the player's currently held item
    /// </summary>

    // TODO: This currently only supports right hand holding and will need to be expanded to support left hand items and dual hand items
    public void DropItem()
    {
        //Unity keeps a destroyed object's reference alive but hollow, and C#'s ?. does not consult
        //Unity's own idea of null, so the reads below would throw from native code
        if (RightHandItemGO == null)
        {
            disabledRHItemColliders.Clear();
            RightHandItemGO = null;
            itemHoldPos = null;
            itemHoldRot = null;
            return;
        }

        // Re-enable previously disabled colliders
        foreach (Collider col in disabledRHItemColliders)
        {
            //The item may be long gone. Reading a name off a destroyed object throws from native
            //code, and the log line asking for it did exactly that before the check below.
            if (col == null)
                continue;

            Multiplayer.LogDebug(() => $"NetworkedPlayer.DropItem() Re-enabling collider: {col.name}, Type: {col.GetType()}");
            col.enabled = true;
        }
        disabledRHItemColliders.Clear();

        var itemGrabHandler = RightHandItemGO.GetComponentInChildren<GrabHandlerItem>();
        if (itemGrabHandler != null)
        {
            itemGrabHandler.TogglePhysics(true);
            itemGrabHandler.interactionAllowed = true;
        }

        RightHandItemGO.transform.SetParent(WorldMover.OriginShiftParent, true);

        RightHandItemGO = null;
        itemHoldPos = null;
        itemHoldRot = null;
    }

}
