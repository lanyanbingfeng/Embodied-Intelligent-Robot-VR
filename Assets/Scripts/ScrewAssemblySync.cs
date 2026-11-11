using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace VRSim
{
    /// <summary>
    /// 单颗螺丝的行程同步：按螺丝刀转角增量推进、绕轴自转、限制在 [minTravel, maxTravel]。
    /// 螺丝的"家"由 <see cref="ScrewAssemblyManager"/> 分配的孔位决定，任意螺丝都能进任意空槽；
    /// 被取走后槽位释放，别的螺丝就可以归位到这里。拧出到底可徒手取走。
    /// </summary>
    [DisallowMultipleComponent]
    public class ScrewAssemblySync : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        ScrewdriverRotationDriver driver;
        [Tooltip("拧出到底后允许抓取的交互组件，为空则不支持徒手取螺丝")]
        [SerializeField]
        XRGrabInteractable screwGrab;

        [Header("Thread Parameters")]
        [Tooltip("螺距：旋转 360° 沿轴推进的距离（米），默认 5mm")]
        [Range(0.0005f, 0.01f)]
        public float pitch = 0.005f;
        [Tooltip("拧出极限（米），0 = 孔位即最外侧，可设负值让它能再退出来一点")]
        [Range(-0.05f, 0.05f)]
        public float minTravel = -0.005f;
        [Tooltip("拧紧极限（米），即到底的位置")]
        [Range(-0.05f, 0.1f)]
        public float maxTravel = 0.025f;
        [Tooltip("旋入方向：角度正向时沿刀轴的符号。刀尖朝下取 -1，刀尖朝上取 +1")]
        public int direction = -1;

        [Header("Screw Slot")]
        [Tooltip("螺丝放回该范围内并松手，会自动归到最近的空孔位（米）")]
        public float returnRadius = 0.08f;
        [Tooltip("归位时允许的最大速度（米/秒），飞过去的不算放回")]
        public float returnMaxSpeed = 1.5f;
        [Tooltip("刀根吸附点距孔位的距离（米），沿插入反方向")]
        public float socketHeight = 0.159f;
        [Tooltip("调试：打印复位判定过程")]
        public bool debugReturn = false;

        Vector3 m_BasePos;
        Quaternion m_BaseRot;
        Vector3 m_AxisWorld;
        float m_Travel;
        float m_PrevAngle;
        bool m_Initialized;
        bool m_Taken;
        bool m_HasEngaged;
        bool m_WasSocketed;
        Rigidbody m_Rb;

        ScrewAssemblyManager m_Manager;
        ScrewAssemblyManager.Slot m_Slot;

        /// <summary>当前行程（米），0 为孔位基准，正值代表已旋入。</summary>
        public float Travel { get { return m_Travel; } }

        /// <summary>是否已被玩家取走。</summary>
        public bool IsTaken { get { return m_Taken; } }

        /// <summary>当前是否占着一个孔位。</summary>
        public bool HasSlot { get { return m_Slot != null && m_Slot.occupant == this; } }

        /// <summary>螺丝刀根部的吸附点：由孔位位置沿插入反方向抬高 socketHeight 得到。</summary>
        public Vector3 SocketPosition
        {
            get
            {
                Vector3 up = driver != null ? -driver.InsertDirection : Vector3.up;
                Vector3 p = m_Slot != null ? m_Slot.position : transform.position;
                return p + up * socketHeight;
            }
        }

        /// <summary>由总管分配孔位时调用。</summary>
        public void BindSlot(ScrewAssemblyManager manager, ScrewAssemblyManager.Slot slot)
        {
            m_Manager = manager;
            m_Slot = slot;
            m_BasePos = slot.position;
            m_BaseRot = slot.rotation;
        }

        /// <summary>螺丝刀当前是不是在操作这一颗螺丝。</summary>
        bool IsDriverActive
        {
            get { return driver != null && driver.ActiveScrew == this; }
        }

        void Awake()
        {
            m_Rb = GetComponent<Rigidbody>();
        }

        void Start()
        {
            // 没有总管时（单螺丝场景）用自身初始位置当孔位
            if (m_Slot == null)
            {
                m_BasePos = transform.position;
                m_BaseRot = transform.rotation;
            }
        }

        void Update()
        {
            if (driver == null)
                return;

            if (!m_Taken && screwGrab != null && screwGrab.isSelected)
            {
                m_Taken = true;
                // 螺丝被拿走了，槽位空出来；只有它正被刀操作时才需要解除行程限位
                if (IsDriverActive)
                    driver.SetAngleLimits(-1e6f, 1e6f);
                if (m_Manager != null)
                {
                    m_Manager.Release(this);
                    m_Slot = null;
                }
            }

            if (m_Taken)
            {
                TryReturnHome();
                return;
            }

            // 只有被刀盯着的那颗才跟角度；其余螺丝保持不动
            bool socketed = IsDriverActive && driver.isSocketed;
            if (socketed && !m_WasSocketed)
            {
                m_PrevAngle = driver.AccumulatedAngle;
                ApplyAngleLimits();
            }
            m_WasSocketed = socketed;

            if (socketed)
            {
                if (!m_Initialized)
                    Initialize();

                float deltaAngle = driver.AccumulatedAngle - m_PrevAngle;
                m_PrevAngle = driver.AccumulatedAngle;
                if (Mathf.Abs(deltaAngle) > 0f)
                {
                    m_Travel += deltaAngle / 360f * pitch * direction;
                    m_Travel = Mathf.Clamp(m_Travel, minTravel, maxTravel);
                }

                if (m_Travel > minTravel + 1e-4f)
                    m_HasEngaged = true;

                ApplyPose();
            }
            else if (!m_Taken && HasSlot)
            {
                // 既没被拿走、也不在拧：螺丝必须牢牢待在孔位上。
                // 否则一旦被外力挪开（物理、手、测试脚本），孔位坐标还在原地，
                // 螺丝刀就会照着孔位吸过去，而螺丝在旁边 —— 表现为"吸附错位"。
                if (m_Initialized)
                    ApplyPose();
                else
                {
                    transform.position = m_BasePos;
                    transform.rotation = m_BaseRot;
                }
            }

            UpdateGrabAvailability();
        }

        void Initialize()
        {
            m_AxisWorld = driver.CurrentAxisWorld.normalized;
            m_PrevAngle = driver.AccumulatedAngle;
            m_Initialized = true;
        }

        void ApplyAngleLimits()
        {
            float p = Mathf.Max(pitch, 1e-5f);
            float toMax = (maxTravel - m_Travel) / p * 360f * direction;
            float toMin = (minTravel - m_Travel) / p * 360f * direction;
            driver.SetAngleLimits(toMin, toMax);
        }

        void ApplyPose()
        {
            float signed = m_Travel * direction;
            float angle = m_Travel / Mathf.Max(pitch, 1e-5f) * 360f * direction;

            transform.position = m_BasePos + m_AxisWorld * signed;
            transform.rotation = Quaternion.AngleAxis(angle, m_AxisWorld) * m_BaseRot;
        }

        /// <summary>螺丝被取走后：松手落地、靠近空孔位就自动归位（任意空槽都可以）。</summary>
        void TryReturnHome()
        {
            bool selected = screwGrab != null && screwGrab.isSelected;
            float speed = m_Rb != null ? m_Rb.linearVelocity.magnitude : 0f;
            if (debugReturn)
                Debug.Log(string.Format("[ScrewReturn] selected={0} speed={1:F2} kin={2} pos={3:F3}",
                    selected, speed, m_Rb != null && m_Rb.isKinematic, transform.position.magnitude));

            if (selected)
                return;

            if (m_Rb != null && m_Rb.isKinematic)
            {
                // 先同步刚体内部位置再切物理，否则会回弹到切换前的位置
                m_Rb.position = transform.position;
                m_Rb.rotation = transform.rotation;
                m_Rb.isKinematic = false;
                m_Rb.useGravity = true;
            }

            if (m_Rb != null && m_Rb.linearVelocity.sqrMagnitude > returnMaxSpeed * returnMaxSpeed)
                return;

            // 距离判定交给 TryOccupy 做：只有确实有空槽、且螺丝在空槽 returnRadius 内才会占用。
            // 绝不能自己先算"最近空槽距离" —— 四个槽都被占时它只会返回螺丝自身位置，
            // 导致最后一颗螺丝被自己吸回孔里，四颗永远拿不完、背板永远解不开。
            if (m_Manager != null)
            {
                ScrewAssemblyManager.Slot slot;
                if (!m_Manager.TryOccupy(this, transform.position, returnRadius, out slot))
                    return;
                BindSlot(m_Manager, slot);
            }
            else if (Vector3.Distance(transform.position, m_BasePos) > returnRadius)
            {
                // 单螺丝模式：不在原位附近就不归位
                return;
            }

            transform.position = m_BasePos;
            transform.rotation = m_BaseRot;

            if (m_Rb != null)
            {
                m_Rb.linearVelocity = Vector3.zero;
                m_Rb.angularVelocity = Vector3.zero;
                m_Rb.isKinematic = true;
                m_Rb.useGravity = false;
            }

            m_Taken = false;
            // 归位后处于"未拧"的极限位置
            m_Travel = Mathf.Min(minTravel, 0f);
            m_HasEngaged = true;
            if (IsDriverActive)
            {
                driver.ResetAccumulatedAngle();
                m_PrevAngle = driver.AccumulatedAngle;
            }
            if (m_Initialized)
                ApplyPose();
            if (IsDriverActive && driver.isSocketed)
                ApplyAngleLimits();
        }

        void UpdateGrabAvailability()
        {
            if (screwGrab == null)
                return;

            bool atOutLimit = m_HasEngaged && m_Travel <= minTravel + 1e-4f;
            if (screwGrab.enabled != atOutLimit)
                screwGrab.enabled = atOutLimit;
        }
    }
}
