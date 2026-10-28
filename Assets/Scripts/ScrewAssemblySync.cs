using UnityEngine;

namespace VRSim
{
    /// <summary>
    /// 螺丝同步：监听螺丝刀旋转驱动（ScrewdriverRotationDriver），
    /// 螺丝同步绕刀轴旋转并按螺距沿轴旋入/旋出，限位 [minTravel, maxTravel]。
    /// driver.isSocketed 为 true 时记录基准开始跟随；解除后保持当前姿态。
    /// </summary>
    [DisallowMultipleComponent]
    public class ScrewAssemblySync : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        ScrewdriverRotationDriver driver;

        [Header("Thread Parameters")]
        [Tooltip("螺距：旋转 360° 沿轴推进的距离（米），默认 1.5mm")]
        [Range(0.0005f, 0.01f)]
        public float pitch = 0.0015f;
        [Tooltip("旋入最小位置（米），0 = 完全旋出")]
        [Range(0f, 0.05f)]
        public float minTravel = 0f;
        [Tooltip("旋入最大位置（米），即拧紧限位")]
        [Range(0f, 0.1f)]
        public float maxTravel = 0.02f;
        [Tooltip("旋入方向：+1 沿刀轴方向推进；-1 反向（逆牙/反手场景）")]
        public int direction = 1;

        Vector3 m_BasePos;
        Quaternion m_BaseRot;
        Vector3 m_AxisWorld;
        bool m_Initialized;

        void Update()
        {
            if (driver == null)
                return;

            if (driver.isSocketed)
            {
                if (!m_Initialized)
                {
                    m_BasePos = transform.position;
                    m_BaseRot = transform.rotation;
                    m_AxisWorld = driver.CurrentAxisWorld;
                    m_Initialized = true;
                }

                float rawTravel = driver.AccumulatedAngle / 360f * pitch;
                float targetTravel = Mathf.Clamp(rawTravel, minTravel, maxTravel);
                float screwAngle = targetTravel / pitch * 360f;

                transform.rotation = m_BaseRot * Quaternion.AngleAxis(screwAngle, m_AxisWorld);
                transform.position = m_BasePos + m_AxisWorld * targetTravel * direction;
            }
            else
            {
                m_Initialized = false;
            }
        }
    }
}