using UnityEngine;
using UnityEngine.Events;

using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace VRSim
{
    /// <summary>
    /// 螺丝刀吸附 + 绕轴自转驱动。
    /// 交互流程：
    /// 1) 手捏住螺丝刀、刀尖靠近 socketPoint（距离小于 snapRadius）→ 立即吸附：
    ///    位置锁到 socketPoint，朝向对齐 socketPoint 的插入方向，不再保留手上姿态。
    /// 2) 吸附期间捏住并转动 → 刀绕自身轴自转；松手 → 停止旋转，刀保持吸附姿态不动。
    /// 3) 捏住后把手拖离起点超过 pulloutDistance → 退出吸附，刀重新跟随手。
    /// </summary>
    [DisallowMultipleComponent]
    public class ScrewdriverRotationDriver : MonoBehaviour
    {
        public enum Axis { X, Y, Z }

        [Header("References")]
        [SerializeField]
        HandRotationSource handRotationSource;
        [SerializeField]
        XRGrabInteractable grabInteractable;
        [SerializeField]
        Rigidbody targetRigidbody;

        [Header("Rotation")]
        [Tooltip("刀轴（本地轴）。吸附后绕该轴自转")]
        public Axis rotationAxis = Axis.Y;
        [Tooltip("旋转灵敏度：1 = 手转 1° 刀转 1°")]
        [Range(0.1f, 4f)]
        public float sensitivity = 1f;
        [Tooltip("死区（度/帧）：忽略小于该值的手部抖动")]
        [Range(0f, 5f)]
        public float deadZoneDegrees = 0.5f;
        [Tooltip("单帧有效旋转上限（度）：超过视为人手不可能的角度，按追踪跳变丢弃")]
        public float maxDeltaPerFrame = 25f;

        [Header("Socket Snap")]
        [Tooltip("可选：绑定螺丝总管后，会自动找最近的在位螺丝进行吸附，四颗螺丝都通用")]
        [SerializeField]
        ScrewAssemblyManager manager;
        [Tooltip("可选：绑定单颗螺丝时，吸附点跟随这颗螺丝（旧用法）")]
        [SerializeField]
        ScrewAssemblySync screwAssembly;
        [Tooltip("吸附目标位置。为空则使用上面的螺丝目标")]
        [SerializeField]
        Transform socketPoint;
        [Tooltip("刀尖插入方向（世界空间）。默认竖直向下，对齐时刀轴会转到它的反方向")]
        public Vector3 insertDirection = new Vector3(0f, -1f, 0f);
        [Tooltip("刀根到刀尖的距离（米），用于把吸附判定点放到刀尖上")]
        public float tipOffset = 0.156f;
        [Tooltip("刀尖到吸附点的距离小于该值时立即吸附（米）")]
        public float snapRadius = 0.06f;
        [Tooltip("捏住后手部相对捏住瞬间的位移超过该值时退出吸附（米）")]
        public float pulloutDistance = 0.15f;
        [Tooltip("调试用：运行后直接进入吸附状态")]
        public bool forceSocketed = false;

        [Header("Debug")]
        [Tooltip("调试：每 0.5 秒往 Console 打印一次吸附判定状态（手是否抓住、刀尖距离、夹角）")]
        public bool debugSnap = false;

        [Header("Events")]
        public UnityEvent<float> onRotated;
        public UnityEvent onRotationStart;
        public UnityEvent onRotationStop;
        public UnityEvent<bool> onSocketedChanged;

        XRGrabInteractable m_Grab;
        Rigidbody m_Rb;
        bool m_Socketed;
        bool m_WasScrewing;
        bool m_WasHolding;
        float m_AccumulatedAngle;
        Quaternion m_BaseRotation;
        Vector3 m_AxisLocal;
        Quaternion m_PrevHandRot;
        bool m_HasPrevHandRot;
        Vector3 m_HandAnchorPos;
        Vector3 m_InsertDir = Vector3.down;
        float m_DebugTimer;
        float m_MinAngle = float.NegativeInfinity;
        float m_MaxAngle = float.PositiveInfinity;
        ScrewAssemblySync m_TargetScrew;

        /// <summary>当前是否处于吸附（拧螺丝）状态。</summary>
        public bool isSocketed { get { return m_Socketed; } }

        /// <summary>自本次吸附起累计旋转角度（度），供螺丝同步使用。</summary>
        public float AccumulatedAngle { get { return m_AccumulatedAngle; } }

        /// <summary>当前刀轴的世界方向。</summary>
        public Vector3 CurrentAxisWorld
        {
            get { return (m_Socketed ? m_BaseRotation : transform.rotation) * m_AxisLocal; }
        }

        /// <summary>吸附点的世界位置：优先跟随当前目标螺丝，其次单颗螺丝，最后用 socketPoint。</summary>
        public Vector3 SnapPointPosition
        {
            get
            {
                if (m_TargetScrew != null)
                    return m_TargetScrew.SocketPosition;
                if (screwAssembly != null)
                    return screwAssembly.SocketPosition;
                return socketPoint != null ? socketPoint.position : transform.position;
            }
        }

        /// <summary>是否配置了可用的吸附点。</summary>
        public bool HasSnapPoint
        {
            get { return manager != null || screwAssembly != null || socketPoint != null; }
        }

        /// <summary>刀尖插入方向（归一化，世界空间）。</summary>
        public Vector3 InsertDirection { get { return m_InsertDir; } }

        /// <summary>
        /// 当前正被这把螺丝刀操作的那颗螺丝。
        /// 只有它才该跟角度变化、才该设置行程限位；其余螺丝必须保持静止。
        /// </summary>
        public ScrewAssemblySync ActiveScrew
        {
            get
            {
                if (m_TargetScrew != null)
                    return m_TargetScrew;
                return screwAssembly; // 单螺丝模式兼容
            }
        }

        /// <summary>
        /// 限制自转角度范围（度，相对本次吸附起点）。由螺丝行程同步调用：
        /// 螺丝拧到极限后，刀就要停住，不能空转。
        /// </summary>
        public void SetAngleLimits(float minAngle, float maxAngle)
        {
            m_MinAngle = Mathf.Min(minAngle, maxAngle);
            m_MaxAngle = Mathf.Max(minAngle, maxAngle);
        }

        /// <summary>把自转计数归零（螺丝复位后调用）。吸附中会顺带把刀转回基准姿态。</summary>
        public void ResetAccumulatedAngle()
        {
            m_AccumulatedAngle = 0f;
            m_HasPrevHandRot = false;
            m_MinAngle = float.NegativeInfinity;
            m_MaxAngle = float.PositiveInfinity;
            if (m_Socketed)
                transform.rotation = m_BaseRotation;
        }

        void Awake()
        {
            m_Grab = grabInteractable != null ? grabInteractable : GetComponent<XRGrabInteractable>();
            m_Rb = targetRigidbody != null ? targetRigidbody : GetComponent<Rigidbody>();
            m_AxisLocal = rotationAxis == Axis.X ? Vector3.right : (rotationAxis == Axis.Y ? Vector3.up : Vector3.forward);
            m_InsertDir = insertDirection.sqrMagnitude > 1e-6f ? insertDirection.normalized : Vector3.down;
            m_BaseRotation = transform.rotation;
        }

        void Start()
        {
            if (forceSocketed)
                EnterSnap();
        }

        void Update()
        {
            if (!m_Socketed)
            {
                if (debugSnap)
                    LogSnapState();
                TrySnap();
                return;
            }

            bool holding = HasHandSelecting();

            // 目标螺丝被拿走了（不在孔位上），立刻退出吸附
            if (m_TargetScrew != null && !m_TargetScrew.HasSlot)
            {
                ExitSnap();
                return;
            }

            if (holding && !m_WasHolding)
            {
                // 每次重新捏住都刷新位移起点与角度基准，避免松手后再捏时跳变
                m_WasHolding = true;
                m_HandAnchorPos = GetHandAttachPosition();
                m_HasPrevHandRot = false;
            }
            else if (!holding)
            {
                m_WasHolding = false;
            }

            LockPose();

            if (holding)
            {
                if (Vector3.Distance(GetHandAttachPosition(), m_HandAnchorPos) > pulloutDistance)
                {
                    ExitSnap();
                    return;
                }

                if (!m_WasScrewing)
                {
                    m_WasScrewing = true;
                    onRotationStart.Invoke();
                }

                UpdateScrewRotation();
            }
            else if (m_WasScrewing)
            {
                // 松手：停止旋转，姿态保持，等待再次捏住继续拧
                m_WasScrewing = false;
                m_HasPrevHandRot = false;
                onRotationStop.Invoke();
            }
        }

        void TrySnap()
        {
            if (!HasSnapPoint || !HasHandSelecting())
                return;

            Vector3 axisNow = CurrentAxisWorld;
            if (axisNow.sqrMagnitude < 1e-6f)
                return;
            axisNow.Normalize();

            Vector3 insertDir = m_InsertDir;
            // 刀身不能横着扫过来：刀轴需大体朝插入反方向
            if (Vector3.Dot(axisNow, -insertDir) < 0.2f)
                return;

            Vector3 tipNow = transform.position - axisNow * tipOffset;

            if (manager != null)
            {
                // 多螺丝：找最近的在位螺丝，刀尖要对准它的吸附点
                ScrewAssemblySync best = null;
                float bestDist = snapRadius;
                var list = manager.OccupiedScrews;
                for (int i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    Vector3 target = s.SocketPosition + insertDir * tipOffset;
                    float d = Vector3.Distance(tipNow, target);
                    if (d <= bestDist)
                    {
                        bestDist = d;
                        best = s;
                    }
                }

                if (best == null)
                    return;

                m_TargetScrew = best;
                EnterSnap();
                return;
            }

            // 单螺丝 / 固定点
            Vector3 tipTarget = SnapPointPosition + insertDir * tipOffset;
            if (Vector3.Distance(tipNow, tipTarget) > snapRadius)
                return;

            EnterSnap();
        }

        /// <summary>
        /// 松手后的兜底：不在吸附状态、也没被握持时，确保刚体回到物理状态。
        /// 否则 XRI 抓取时若记录到 kinematic=true（例如从吸附状态里抓起来），
        /// 松手会把它恢复成 kinematic，刀就悬在空中了。
        /// </summary>
        void LateUpdate()
        {
            if (m_Socketed || m_Grab == null || m_Grab.isSelected)
                return;

            if (!m_Grab.trackPosition || !m_Grab.trackRotation)
            {
                m_Grab.trackPosition = true;
                m_Grab.trackRotation = true;
            }

            if (m_Rb != null && m_Rb.isKinematic)
            {
                m_Rb.isKinematic = false;
                m_Rb.useGravity = true;
            }
        }

        void EnterSnap()
        {
            if (m_Socketed || !HasSnapPoint)
                return;

            m_Socketed = true;
            m_AccumulatedAngle = 0f;
            m_MinAngle = float.NegativeInfinity;
            m_MaxAngle = float.PositiveInfinity;
            m_HasPrevHandRot = false;
            m_WasScrewing = false;
            m_WasHolding = false;
            m_BaseRotation = GetAlignedRotation();

            if (m_Grab != null)
            {
                m_Grab.trackPosition = false;
                m_Grab.trackRotation = false;
            }
            if (m_Rb != null)
            {
                m_Rb.linearVelocity = Vector3.zero;
                m_Rb.angularVelocity = Vector3.zero;
                m_Rb.isKinematic = true;
                m_Rb.useGravity = false;
            }

            transform.position = SnapPointPosition;
            transform.rotation = m_BaseRotation;
            onSocketedChanged.Invoke(true);
        }

        void ExitSnap()
        {
            if (!m_Socketed)
                return;

            m_Socketed = false;
            m_WasScrewing = false;
            m_WasHolding = false;
            m_HasPrevHandRot = false;
            m_TargetScrew = null;

            if (m_Grab != null)
            {
                m_Grab.trackPosition = true;
                m_Grab.trackRotation = true;
            }
            if (m_Rb != null)
            {
                m_Rb.isKinematic = false;
                m_Rb.useGravity = true;
            }

            onRotationStop.Invoke();
            onSocketedChanged.Invoke(false);
        }

        /// <summary>
        /// 吸附姿态：让刀轴对齐插入方向的反方向（刀柄朝外、刀尖朝孔内），
        /// 绕轴朝向沿用当前手上姿态，视觉上不会突跳。
        /// </summary>
        Quaternion GetAlignedRotation()
        {
            Vector3 targetAxis = -m_InsertDir;
            Vector3 currentAxis = transform.rotation * m_AxisLocal;
            if (currentAxis.sqrMagnitude < 1e-6f || targetAxis.sqrMagnitude < 1e-6f)
                return transform.rotation;

            return Quaternion.FromToRotation(currentAxis, targetAxis.normalized) * transform.rotation;
        }

        void LogSnapState()
        {
            m_DebugTimer += Time.unscaledDeltaTime;
            if (m_DebugTimer < 0.5f)
                return;
            m_DebugTimer = 0f;

            if (!HasSnapPoint)
            {
                Debug.Log("[Screwdriver] 吸附点未设置，无法吸附");
                return;
            }

            Vector3 axisNow = CurrentAxisWorld.normalized;
            Vector3 tipNow = transform.position - axisNow * tipOffset;
            Vector3 tipTarget = SnapPointPosition + m_InsertDir * tipOffset;
            Debug.Log(string.Format(
                "[Screwdriver] selecting={0} holding={1} tipDist={2:F3} snapRadius={3:F3} axisDot={4:F2}",
                m_Grab != null ? m_Grab.interactorsSelecting.Count : -1,
                HasHandSelecting(),
                Vector3.Distance(tipNow, tipTarget),
                snapRadius,
                Vector3.Dot(axisNow, -m_InsertDir)));
        }

        /// <summary>在 Scene 视图里画出吸附判定球，方便现场调 snapRadius。</summary>
        void OnDrawGizmosSelected()
        {
            Vector3 insertDir = insertDirection.sqrMagnitude > 1e-6f ? insertDirection.normalized : Vector3.down;

            Vector3 basePos;
            if (Application.isPlaying)
                basePos = SnapPointPosition;
            else if (screwAssembly != null)
                basePos = screwAssembly.transform.position - insertDir * screwAssembly.socketHeight;
            else if (socketPoint != null)
                basePos = socketPoint.position;
            else
                basePos = transform.position;

            Vector3 tipTarget = basePos + insertDir * tipOffset;

            Gizmos.color = new Color(1f, 0.65f, 0.1f, 0.35f);
            Gizmos.DrawSphere(tipTarget, snapRadius);
            Gizmos.color = new Color(1f, 0.65f, 0.1f, 1f);
            Gizmos.DrawLine(basePos, tipTarget);
        }

        /// <summary>吸附期间每帧把位置钉在 socket 上（松手后同样保持）。</summary>
        void LockPose()
        {
            if (!HasSnapPoint)
                return;

            transform.position = SnapPointPosition;
            if (m_Rb != null && !m_Rb.isKinematic)
                m_Rb.isKinematic = true;
        }

        bool HasHandSelecting()
        {
            if (m_Grab == null) return false;
            foreach (var i in m_Grab.interactorsSelecting)
                if (!(i is XRSocketInteractor))
                    return true;
            return false;
        }

        Vector3 GetHandAttachPosition()
        {
            if (m_Grab == null) return transform.position;
            foreach (var i in m_Grab.interactorsSelecting)
            {
                if (i is XRSocketInteractor) continue;
                var baseInter = i as XRBaseInteractor;
                if (baseInter != null && baseInter.attachTransform != null)
                    return baseInter.attachTransform.position;
                return i.transform.position;
            }
            return transform.position;
        }

        void UpdateScrewRotation()
        {
            if (handRotationSource == null || !handRotationSource.IsTracking)
                return;

            Quaternion cur = handRotationSource.CurrentRotation;
            if (!m_HasPrevHandRot)
            {
                m_PrevHandRot = cur;
                m_HasPrevHandRot = true;
                return;
            }

            Quaternion delta = cur * Quaternion.Inverse(m_PrevHandRot);
            m_PrevHandRot = cur;

            float ang;
            Vector3 axis;
            delta.ToAngleAxis(out ang, out axis);
            if (ang > 180f)
            {
                ang = 360f - ang;
                axis = -axis;
            }
            if (ang < 0.0001f)
                return;

            Vector3 axisWorld = m_BaseRotation * m_AxisLocal;
            float dTheta = ang * Vector3.Dot(axis, axisWorld);
            if (Mathf.Abs(dTheta) < deadZoneDegrees)
                return;
            if (Mathf.Abs(dTheta) > maxDeltaPerFrame)
                return;

            float applied = dTheta * sensitivity;
            float before = m_AccumulatedAngle;
            float next = Mathf.Clamp(before + applied, m_MinAngle, m_MaxAngle);
            float realDelta = next - before;
            if (Mathf.Abs(realDelta) < 1e-4f)
                return; // 已到螺丝行程极限，刀到此为止，不再空转

            m_AccumulatedAngle = next;
            transform.rotation = m_BaseRotation * Quaternion.AngleAxis(m_AccumulatedAngle, m_AxisLocal);
            onRotated.Invoke(realDelta);
        }
    }
}
