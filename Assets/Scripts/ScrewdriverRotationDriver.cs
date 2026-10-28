using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace VRSim
{
    /// <summary>
    /// 螺丝刀绕轴旋转驱动：右手捏住（select 激活）且处于吸附状态时，
    /// 把手的旋转增量投影到螺丝刀长轴（默认本地 Z），驱动螺丝刀绕轴自转。
    /// 位置由 XR Socket Interactor 锁定，本脚本只负责旋转。
    /// 吸附状态自动监听 XR Socket Interactor 的 select 事件；forceSocketed 用于编辑器调试。
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
        public Axis rotationAxis = Axis.Z;
        [Tooltip("旋转灵敏度：1 = 手转 1° 刀转 1°（现实比例）")]
        [Range(0.1f, 4f)]
        public float sensitivity = 1f;
        [Tooltip("死区（度/帧）：忽略小于该值的手部抖动")]
        [Range(0f, 5f)]
        public float deadZoneDegrees = 0.5f;

        [Header("Socket")]
        [Tooltip("调试用：无 Socket 时模拟已吸附，便于编辑器验证")]
        public bool forceSocketed = false;
        [Tooltip("自动集成：XR Socket Interactor（可选引用；不填则自动识别被 Socket 选中）")]
        public XRSocketInteractor socketInteractor;
        [Tooltip("拔出距离（米）：吸附状态下手拉离超过该距离则解除吸附（重新拿起）")]
        public float pulloutDistance = 0.08f;

        [Header("Events")]
        public UnityEvent<float> onRotated;
        public UnityEvent onRotationStart;
        public UnityEvent onRotationStop;

        XRGrabInteractable m_Grab;
        Rigidbody m_Rb;
        bool m_Socketed;
        bool m_WasScrewing;
        float m_AccumulatedAngle;
        Quaternion m_BaseRotation;
        Vector3 m_AxisLocal;
        Quaternion m_PrevHandRot;
        bool m_HasPrevHandRot;
        Vector3 m_LockedPosition;

        /// <summary>当前是否处于吸附状态（forceSocketed 或 Socket 自动吸附）。</summary>
        public bool isSocketed { get { return m_Socketed || forceSocketed; } }

        /// <summary>累计旋转角度（度），供螺丝同步使用。</summary>
        public float AccumulatedAngle { get { return m_AccumulatedAngle; } }

        /// <summary>当前旋转轴的世界方向（吸附基准姿态下的局部轴）。</summary>
        public Vector3 CurrentAxisWorld
        {
            get
            {
                return (m_Socketed || forceSocketed ? m_BaseRotation : transform.rotation) * m_AxisLocal;
            }
        }

        void Awake()
        {
            m_Grab = grabInteractable != null ? grabInteractable : GetComponent<XRGrabInteractable>();
            m_Rb = targetRigidbody != null ? targetRigidbody : GetComponent<Rigidbody>();
            m_AxisLocal = rotationAxis == Axis.X ? Vector3.right : (rotationAxis == Axis.Y ? Vector3.up : Vector3.forward);
            m_BaseRotation = transform.rotation;
        }

        void OnEnable()
        {
            if (m_Grab != null)
            {
                m_Grab.selectEntered.AddListener(OnSelectEntered);
                m_Grab.selectExited.AddListener(OnSelectExited);
            }
        }

        void OnDisable()
        {
            if (m_Grab != null)
            {
                m_Grab.selectEntered.RemoveListener(OnSelectEntered);
                m_Grab.selectExited.RemoveListener(OnSelectExited);
            }
        }

        void Start()
        {
            // 场景加载时 Socket 可能已自动选中螺丝刀（螺丝刀初始放在 Socket 上）
            if (m_Grab != null && m_Grab.isSelected)
            {
                foreach (var i in m_Grab.interactorsSelecting)
                {
                    if (i is XRSocketInteractor)
                    {
                        var sock = (XRSocketInteractor)i;
                        var attach = sock.attachTransform != null ? sock.attachTransform : sock.transform;
                        m_LockedPosition = attach.position;
                        transform.position = attach.position;
                        SetSocketed(true);
                        break;
                    }
                }
            }
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (args.interactorObject is XRSocketInteractor sock)
            {
                var attach = sock.attachTransform != null ? sock.attachTransform : sock.transform;
                m_LockedPosition = attach.position;
                transform.position = attach.position;
                SetSocketed(true);
            }
        }

        void OnSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject is XRSocketInteractor)
                SetSocketed(false);
        }

        /// <summary>
        /// 外部（Socket 实现）调用：吸附完成传 true，解除传 false。
        /// 使用 XR Socket Interactor 时通常无需手动调用（脚本自动监听 select 事件）。
        /// </summary>
        public void SetSocketed(bool socketed)
        {
            if (socketed == m_Socketed)
                return;
            m_Socketed = socketed;

            if (socketed)
            {
                m_BaseRotation = transform.rotation;
                m_AccumulatedAngle = 0f;
                m_HasPrevHandRot = false;
                if (m_Grab != null)
                {
                    m_Grab.trackPosition = false;
                    m_Grab.trackRotation = false;
                }
                if (m_Rb != null)
                {
                    m_Rb.isKinematic = true;
                    m_Rb.useGravity = false;
                }
            }
            else
            {
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
            }
        }

        void Update()
        {
            if (!isSocketed)
                return;

            bool handSelecting = HasHandSelecting();
            bool screwing = handSelecting;

            // 拔出检测：真实吸附状态下手拉离锁定位置超过阈值 → 解除吸附（重新拿起）
            if (m_Socketed && screwing &&
                Vector3.Distance(GetHandAttachPosition(), m_LockedPosition) > pulloutDistance)
            {
                ReleaseSocketSelection();
                SetSocketed(false);
                return;
            }

            if (screwing)
            {
                if (!m_WasScrewing)
                {
                    m_WasScrewing = true;
                    m_HasPrevHandRot = false;
                    onRotationStart.Invoke();
                }
                if (m_Socketed)
                    transform.position = m_LockedPosition;
                UpdateScrewRotation();
            }
            else if (m_WasScrewing)
            {
                m_WasScrewing = false;
                onRotationStop.Invoke();
            }
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

        void ReleaseSocketSelection()
        {
            if (m_Grab == null) return;
            foreach (var i in m_Grab.interactorsSelecting)
            {
                if (i is XRSocketInteractor sock)
                {
                    if (sock.interactionManager != null)
                        sock.interactionManager.SelectExit((IXRSelectInteractor)sock, (IXRSelectInteractable)m_Grab);
                }
            }
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
            if (ang < 0.0001f)
                return;

            Vector3 axisWorld = m_BaseRotation * m_AxisLocal;
            float dTheta = ang * Vector3.Dot(axis, axisWorld);
            if (Mathf.Abs(dTheta) < deadZoneDegrees)
                return;

            float applied = dTheta * sensitivity;
            m_AccumulatedAngle += applied;
            transform.rotation = m_BaseRotation * Quaternion.AngleAxis(m_AccumulatedAngle, m_AxisLocal);
            onRotated.Invoke(applied);
        }
    }
}
