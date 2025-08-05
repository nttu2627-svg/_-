using UnityEngine;

public class NewMonoBehaviourScript : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Debug.Log("Hello from VS Code + Unity!");
        transform.position = new Vector3(1, 2, 3); // ← 測試補全
    }

    // Update is called once per frame
    void Update()
    {
        transform.Rotate(0, 0, 20 * Time.deltaTime);
    }
}


