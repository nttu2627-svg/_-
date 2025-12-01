using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;

public class SubwayCrowdManager : MonoBehaviour
{
    [Header("Configuration")]
    public GameObject agentPrefab;
    public BoxCollider shelterZone; // 避難所區域
    public int totalAgents = 100;
    public Transform agentParent; // Optional: to keep hierarchy clean

    [Header("MBTI Settings")]
    [Range(0f, 1f)]
    public float extrovertRatio = 0.5f;

    void Start()
    {
        if (shelterZone == null)
        {
            Debug.LogError("[SubwayCrowdManager] Shelter Zone (BoxCollider) is not assigned!");
            return;
        }
        GenerateCrowd();
    }

    public void GenerateCrowd()
    {
        for (int i = 0; i < totalAgents; i++)
        {
            // Determine MBTI type based on ratio
            string mbti = (Random.value < extrovertRatio) ? "E" : "I"; 
            
            Vector3 spawnPos = CalculatePositionByPersonality(mbti, shelterZone);
            Quaternion rot = Quaternion.Euler(0, Random.Range(0, 360), 0);
            
            GameObject obj = Instantiate(agentPrefab, spawnPos, rot);
            if (agentParent != null) obj.transform.SetParent(agentParent);

            AgentController ctrl = obj.GetComponent<AgentController>();
            if (ctrl != null)
            {
                // Assign a dummy MBTI for now, can be expanded
                ctrl.agentName = $"Agent_{i}_{mbti}";
                // We might need to expose a public field for MBTI in AgentController if we want to store it
                // For now, we just use the name or we can add a property later.
            }
        }
    }

    Vector3 CalculatePositionByPersonality(string type, BoxCollider zone)
    {
        Bounds b = zone.bounds;
        Vector3 result = Vector3.zero;

        if (type.StartsWith("E"))
        {
            // Extrovert (E): 向心分佈 (Centripetal)
            // 使用 Box-Muller 變換或其他高斯分佈算法，使點更集中於中心
            // 這裡使用簡化的疊加平均法模擬中心極限定理
            float x = (Random.Range(b.min.x, b.max.x) + Random.Range(b.min.x, b.max.x)) / 2;
            float z = (Random.Range(b.min.z, b.max.z) + Random.Range(b.min.z, b.max.z)) / 2;
            result = new Vector3(x, b.center.y, z);
        }
        else
        {
            // Introvert (I): 離心/貼牆分佈 (Centrifugal)
            // 策略：先在邊界框的"表面"隨機取點，然後使用 ClosestPoint 貼合
            
            // 1. 隨機選擇四面牆之一
            int wall = Random.Range(0, 4);
            float rx = 0, rz = 0;
            float margin = 0.5f; // 離牆距離

            switch(wall)
            {
                case 0: rx = b.min.x + margin; rz = Random.Range(b.min.z, b.max.z); break; // 左牆
                case 1: rx = b.max.x - margin; rz = Random.Range(b.min.z, b.max.z); break; // 右牆
                case 2: rx = Random.Range(b.min.x, b.max.x); rz = b.min.z + margin; break; // 後牆
                case 3: rx = Random.Range(b.min.x, b.max.x); rz = b.max.z - margin; break; // 前牆
            }
            
            result = new Vector3(rx, b.center.y, rz);
        }
        
        // 確保點在 NavMesh 上 (Sampling)
        if (NavMesh.SamplePosition(result, out NavMeshHit hit, 2.0f, NavMesh.AllAreas))
        {
            return hit.position;
        }
        
        // Fallback: if sampling fails, return the calculated position (might be slightly off-mesh but better than (0,0,0))
        return result; 
    }
}
