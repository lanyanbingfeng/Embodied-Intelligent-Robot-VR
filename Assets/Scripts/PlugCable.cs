using UnityEngine;

namespace VRSim
{
    /// <summary>
    /// 插头连线：用 LineRenderer 把固定的线根（公口线 / 母口线）连到插头，
    /// 中间点按距离下垂，看起来像一根真实的线材。始终跟随插头移动。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlugCable : MonoBehaviour
    {
        [Tooltip("线的固定端（留空则用本物体自身位置）")]
        [SerializeField]
        Transform anchor;
        [Tooltip("跟随的一端（插头）")]
        [SerializeField]
        Transform plug;

        [Header("Cable Visual")]
        [Tooltip("下垂量（米）")]
        public float sag = 0.05f;
        [Tooltip("线宽（米）")]
        public float width = 0.006f;
        public Color color = new Color(0.07f, 0.07f, 0.08f);

        LineRenderer m_Line;
        Material m_Material;

        void Awake()
        {
            m_Line = GetComponent<LineRenderer>();
            if (m_Line == null)
                m_Line = gameObject.AddComponent<LineRenderer>();

            m_Line.useWorldSpace = true;
            m_Line.positionCount = 3;
            m_Line.startWidth = width;
            m_Line.endWidth = width;
            m_Line.numCapVertices = 4;
            m_Line.numCornerVertices = 2;
            m_Line.alignment = LineAlignment.View;
            m_Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Line.receiveShadows = false;

            m_Material = CreateLineMaterial();
            m_Line.material = m_Material;

            UpdateLine();
        }

        void OnDestroy()
        {
            if (m_Material != null)
                Destroy(m_Material);
        }

        void LateUpdate()
        {
            UpdateLine();
        }

        void UpdateLine()
        {
            if (m_Line == null) return;

            Vector3 a = anchor != null ? anchor.position : transform.position;
            Vector3 b = plug != null ? plug.position : a;

            float dist = Vector3.Distance(a, b);
            Vector3 mid = (a + b) * 0.5f + Vector3.down * sag * Mathf.Clamp01(dist / 0.25f);

            m_Line.SetPosition(0, a);
            m_Line.SetPosition(1, mid);
            m_Line.SetPosition(2, b);
        }

        Material CreateLineMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            mat.color = color;
            return mat;
        }
    }
}
