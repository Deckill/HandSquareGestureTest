using System.Collections;
using System.IO;
using UnityEngine;

namespace ARGestureApp
{
    public class ScreenCaptureManager : MonoBehaviour
    {
        [Header("UI Reference")]
        [Tooltip("화면에 임시로 캡처 영역을 보여줄 UI 패널이 있다면 연결해주세요. (옵션)")]
        public RectTransform captureAreaRectUI;

        [Header("Settings")]
        public string saveFolderName = "ARCaptures";

        private bool isCapturing = false;

        public void TakeSquarePhoto(Vector2[] corners)
        {
            if (isCapturing || corners == null || corners.Length != 4) return;

            // Optional: UI에서 시각적으로 영역을 확인하기 위해 위치 업데이트 (참고: 기울임은 지원 안 함)
            if (captureAreaRectUI != null)
            {
                // 중심점 계산
                Vector2 center = (corners[0] + corners[1] + corners[2] + corners[3]) / 4f;
                float width = Vector2.Distance(corners[0], corners[1]);
                float height = Vector2.Distance(corners[0], corners[3]);

                captureAreaRectUI.position = center;
                captureAreaRectUI.sizeDelta = new Vector2(width, height);
            }

            StartCoroutine(CaptureRoutine(corners));
        }

        private IEnumerator CaptureRoutine(Vector2[] corners)
        {
            isCapturing = true;

            // 렌더링이 모두 완료될 때까지 대기
            yield return new WaitForEndOfFrame();

            // 1. 네 모서리의 AABB (가장 큰 테두리 사각형) 계산
            float minX = corners[0].x, maxX = corners[0].x, minY = corners[0].y, maxY = corners[0].y;
            for (int i = 1; i < 4; i++)
            {
                if (corners[i].x < minX) minX = corners[i].x;
                if (corners[i].x > maxX) maxX = corners[i].x;
                if (corners[i].y < minY) minY = corners[i].y;
                if (corners[i].y > maxY) maxY = corners[i].y;
            }

            int aabbX = Mathf.RoundToInt(minX);
            int aabbY = Mathf.RoundToInt(minY);
            int aabbW = Mathf.RoundToInt(maxX - minX);
            int aabbH = Mathf.RoundToInt(maxY - minY);

            // 안전장치: 화면 범위를 벗어나지 않게 처리
            if (aabbX < 0) { aabbW += aabbX; aabbX = 0; }
            if (aabbY < 0) { aabbH += aabbY; aabbY = 0; }
            if (aabbX + aabbW > Screen.width) aabbW = Screen.width - aabbX;
            if (aabbY + aabbH > Screen.height) aabbH = Screen.height - aabbY;

            if (aabbW <= 0 || aabbH <= 0)
            {
                Debug.LogWarning("캡처 AABB 영역이 유효하지 않습니다.");
                isCapturing = false;
                yield break;
            }

            // 2. AABB 영역 전체 화면 캡처
            Texture2D aabbTex = new Texture2D(aabbW, aabbH, TextureFormat.RGB24, false);
            aabbTex.ReadPixels(new Rect(aabbX, aabbY, aabbW, aabbH), 0, 0);
            aabbTex.Apply();

            // 3. 비스듬한 사각형 모양에 맞게 왜곡(Warp) 변형한 최종 이미지 생성
            int finalW = Mathf.RoundToInt(Vector2.Distance(corners[0], corners[1]));
            int finalH = Mathf.RoundToInt(Vector2.Distance(corners[0], corners[3]));

            if (finalW > 0 && finalH > 0)
            {
                Texture2D photoTexture = new Texture2D(finalW, finalH, TextureFormat.RGB24, false);
                Color32[] aabbPixels = aabbTex.GetPixels32();
                Color32[] finalPixels = new Color32[finalW * finalH];

                // 쌍선형 보간(Bilinear interpolation)을 활용한 픽셀 매핑
                for (int y = 0; y < finalH; y++)
                {
                    float v = (float)y / finalH;
                    // 좌측 테두리와 우측 테두리의 임의 높이(v)에서의 좌표
                    Vector2 p_left = Vector2.Lerp(corners[0], corners[3], v);
                    Vector2 p_right = Vector2.Lerp(corners[1], corners[2], v);

                    for (int x = 0; x < finalW; x++)
                    {
                        float u = (float)x / finalW;
                        // 현재 픽셀(x, y)에 해당하는 원본 화면의 진짜 좌표
                        Vector2 p = Vector2.Lerp(p_left, p_right, u);
                        
                        // AABB 텍스처 상의 상대적 좌표로 변환
                        int px = Mathf.RoundToInt(p.x) - aabbX;
                        int py = Mathf.RoundToInt(p.y) - aabbY;
                        
                        // 안전장치
                        px = Mathf.Clamp(px, 0, aabbW - 1);
                        py = Mathf.Clamp(py, 0, aabbH - 1);

                        finalPixels[y * finalW + x] = aabbPixels[py * aabbW + px];
                    }
                }

                photoTexture.SetPixels32(finalPixels);
                photoTexture.Apply();

                // 파일 생성
                SaveImageToFile(photoTexture);

                // 메모리 해제
                Destroy(photoTexture);
            }

            Destroy(aabbTex);
            isCapturing = false;
        }

        private void SaveImageToFile(Texture2D texture)
        {
            byte[] bytes = texture.EncodeToPNG();
            
            string directoryPath = Path.Combine(Application.persistentDataPath, saveFolderName);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            string timeStamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"PhotoFrame_{timeStamp}.png";
            string filePath = Path.Combine(directoryPath, fileName);

            File.WriteAllBytes(filePath, bytes);
            Debug.Log($"[ScreenCaptureManager] 사진이 성공적으로 저장되었습니다!\n저장 경로: {filePath}");
        }
    }
}
