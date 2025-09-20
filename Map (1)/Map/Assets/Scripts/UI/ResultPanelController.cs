using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Newtonsoft.Json;

namespace DisasterSimulation.UI
{
    /// <summary>
    /// 解析災後評分訊息並動態建立表格，提供匯出功能。
    /// </summary>
    public class ResultPanelController : MonoBehaviour
    {
        public Transform tableRoot;
        public GameObject rowPrefab;

        private Dictionary<string, ScoreDetail> _scores;

        private void OnEnable()
        {
            SimulationClient.OnEvaluationReceived += HandleEvaluation;
        }

        private void OnDisable()
        {
            SimulationClient.OnEvaluationReceived -= HandleEvaluation;
        }

        private void HandleEvaluation(EvaluationReport report)
        {
            _scores = report.Scores;
            foreach (Transform child in tableRoot)
            {
                Destroy(child.gameObject);
            }
            foreach (var kvp in report.Scores)
            {
                var row = Instantiate(rowPrefab, tableRoot);
                var texts = row.GetComponentsInChildren<Text>();
                if (texts.Length >= 5)
                {
                    texts[0].text = kvp.Key;
                    texts[1].text = kvp.Value.LossScore.ToString("F1");
                    texts[2].text = kvp.Value.ResponseScore.ToString("F1");
                    texts[3].text = kvp.Value.CoopScore.ToString("F1");
                    texts[4].text = kvp.Value.TotalScore.ToString("F1");
                }
            }
        }

        public void ExportJson(string fileName = "result.json")
        {
            if (_scores == null) return;
            var json = JsonConvert.SerializeObject(_scores, Formatting.Indented);
            var path = Path.Combine(Application.persistentDataPath, fileName);
            File.WriteAllText(path, json, Encoding.UTF8);
            Debug.Log($"[ResultPanel] JSON exported to {path}");
        }

        public void ExportCsv(string fileName = "result.csv")
        {
            if (_scores == null) return;
            var sb = new StringBuilder();
            sb.AppendLine("Agent,Loss,Response,Coop,Total,Notes");
            foreach (var kvp in _scores)
            {
                var s = kvp.Value;
                sb.AppendLine($"{kvp.Key},{s.LossScore},{s.ResponseScore},{s.CoopScore},{s.TotalScore},\"{s.Notes}\"");
            }
            var path = Path.Combine(Application.persistentDataPath, fileName);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[ResultPanel] CSV exported to {path}");
        }
    }
}