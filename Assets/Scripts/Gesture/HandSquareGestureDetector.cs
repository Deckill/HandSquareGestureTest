using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Mediapipe;

using Color = UnityEngine.Color;
namespace ARGestureApp
{
    [System.Serializable]
    public class OnSquareGestureQuadEvent : UnityEvent<Vector2[]> { }

    public class HandSquareGestureDetector : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("제스처를 유지해야 하는 시간 (초)")]
        public float captureHoldDuration = 1.0f;
        
        [Tooltip("직각 및 평행 판별 허용 오차 (0.0=엄격, 1.0=느슨)")]
        public float angleTolerance = 0.35f;

        [Tooltip("손가락 연장선상에서의 최대 허용범위 (숫자가 클수록 더 멀리 떨어진 손가락 연장선도 인식)")]
        public float intersectionForwardLimit = 15.0f;
        
        [Tooltip("손가락 아래(손목 방향)로의 교차점 허용범위")]
        public float intersectionBackwardLimit = -1.0f;

        [Tooltip("MediaPipe의 손떨림(Jitter)으로 인해 제스처가 끊기는 걸 무시해 주는 시간 (초)")]
        public float gestureJitterGracePeriod = 0.2f;
        
        [Header("Events")]
        // 디버깅 정보 출력용
        public bool showDebugGUI = true;
        private string _debugInfo = "대기 중...";

        public OnSquareGestureQuadEvent OnSquareGestureDetected;
        public UnityEvent OnSquareGestureLost;

        private float currentHoldTime = 0f;
        private float gestureLostTime = 0f;     // 제스처가 끊긴 시간을 측정
        private bool isGestureActive = false;

        // UI/시각화용
        private LineRenderer frameLineRenderer;
        private ScreenCaptureManager _captureManager;
        
        [Tooltip("라인 렌더러가 카메라 앞에 그려질 거리. 너무 작으면 안 보일 수 있습니다.")]
        public float lineDrawDistance = 1.0f;

        // 안전한 스레드 통신용 변수
        private readonly object _resultLock = new object();
        private Mediapipe.Tasks.Vision.HandLandmarker.HandLandmarkerResult _latestResult;
        private bool _hasNewResult = false;

        private void Start()
        {
            // 씬에 있는 HandLandmarkerRunner를 자동으로 찾아 구독합니다.
            var runner = FindObjectOfType<Mediapipe.Unity.Sample.HandLandmarkDetection.HandLandmarkerRunner>();
            if (runner != null)
            {
                runner.OnHandLandmarkDetectionOutputAction += OnHandLandmarksBackground;
                Debug.Log("[HandSquare] 성공적으로 HandLandmarkerRunner에 백그라운드 Action을 구독했습니다!");
            }
            else
            {
                Debug.LogError("[HandSquare] 씬에 HandLandmarkerRunner를 찾을 수 없습니다.");
            }

            // 프리뷰용 CaptureManager 찾기
            _captureManager = FindObjectOfType<ScreenCaptureManager>();

            // 시각 피드백용 라인 렌더러 자동 생성
            frameLineRenderer = gameObject.GetComponent<LineRenderer>();
            if (frameLineRenderer == null)
                frameLineRenderer = gameObject.AddComponent<LineRenderer>();
            
            frameLineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            frameLineRenderer.startColor = Color.green;
            frameLineRenderer.endColor = Color.green;
            frameLineRenderer.startWidth = 0.015f;
            frameLineRenderer.endWidth = 0.015f;
            frameLineRenderer.loop = true;
            frameLineRenderer.positionCount = 4;
            frameLineRenderer.enabled = false;
            frameLineRenderer.useWorldSpace = true;
            frameLineRenderer.sortingOrder = 999;
        }

        private void OnDestroy()
        {
            var runner = FindObjectOfType<Mediapipe.Unity.Sample.HandLandmarkDetection.HandLandmarkerRunner>();
            if (runner != null)
            {
                runner.OnHandLandmarkDetectionOutputAction -= OnHandLandmarksBackground;
            }
        }

        // 백그라운드 스레드에서 콜백으로 들어옴 (Unity API 호출 불가 구간)
        private void OnHandLandmarksBackground(Mediapipe.Tasks.Vision.HandLandmarker.HandLandmarkerResult result)
        {
            lock (_resultLock)
            {
                result.CloneTo(ref _latestResult);
                _hasNewResult = true;
            }
        }

        // 메인 스레드 안전 구역
        private void Update()
        {
            Mediapipe.Tasks.Vision.HandLandmarker.HandLandmarkerResult resultToProcess = default;
            bool shouldProcess = false;

            lock (_resultLock)
            {
                if (_hasNewResult)
                {
                    resultToProcess = _latestResult;
                    _hasNewResult = false;
                    shouldProcess = true;
                }
            }

            if (shouldProcess)
            {
                ProcessHandLandmarksMainThread(resultToProcess);
            }
        }

        private void OnGUI()
        {
            if (!showDebugGUI) return;

            GUIStyle style = new GUIStyle();
            style.fontSize = 24;
            style.normal.textColor = Color.yellow;
            
            // 검은색 배경 박스
            GUI.Box(new UnityEngine.Rect(10, 10, 500, 300), "");
            GUI.Label(new UnityEngine.Rect(20, 20, 480, 280), _debugInfo, style);
        }

        private void ProcessHandLandmarksMainThread(Mediapipe.Tasks.Vision.HandLandmarker.HandLandmarkerResult result)
        {
            if (result.handLandmarks == null || result.handLandmarks.Count != 2)
            {
                HandleGestureGracePeriod();
                return;
            }

            var hand1 = result.handLandmarks[0];
            var hand2 = result.handLandmarks[1];

            if (hand1.landmarks == null || hand1.landmarks.Count < 21 || 
                hand2.landmarks == null || hand2.landmarks.Count < 21)
            {
                HandleGestureGracePeriod();
                return;
            }

            if (TryDetectFrameGesture(hand1, hand2, out Vector2[] captureQuad))
            {
                // 성공적으로 제스처 인식됨 - 끊김 타이머 초기화
                gestureLostTime = 0f;
                currentHoldTime += Time.deltaTime;

                // 라인 렌더러 노출
                if (frameLineRenderer != null && Camera.main != null)
                {
                    frameLineRenderer.enabled = true;
                    // 진행도(0~1)에 따라 라인 렌더러 색상 변화 (진행될 수록 초록 -> 빨강/흰색 등으로 바꿀 수도 있습니다)
                    float progress = Mathf.Clamp01(currentHoldTime / captureHoldDuration);
                    frameLineRenderer.startColor = Color.Lerp(Color.yellow, Color.red, progress);
                    frameLineRenderer.endColor = Color.Lerp(Color.yellow, Color.red, progress);

                    for (int i = 0; i < 4; i++)
                    {
                        Vector3 worldPos = Camera.main.ScreenToWorldPoint(new Vector3(captureQuad[i].x, captureQuad[i].y, lineDrawDistance));
                        frameLineRenderer.SetPosition(i, worldPos);
                    }
                }

                // 프리뷰 업데이트
                if (_captureManager != null)
                {
                    _captureManager.UpdatePreview(captureQuad);
                }
                
                if (currentHoldTime >= captureHoldDuration && !isGestureActive)
                {
                    isGestureActive = true;
                    OnSquareGestureDetected?.Invoke(captureQuad);
                    Debug.Log($"교차 사각 프레임 제스처 인식 완료! ({currentHoldTime:F2}초 유지)");
                    
                    // 캡처하는 순간 잠깐 끄기 (혹은 효과 연출)
                    if (frameLineRenderer != null) frameLineRenderer.enabled = false;
                    if (_captureManager != null) _captureManager.HidePreview();
                }
            }
            else
            {
                HandleGestureGracePeriod();
            }
        }

        private void HandleGestureGracePeriod()
        {
            if (currentHoldTime > 0f)
            {
                gestureLostTime += Time.deltaTime;
                if (gestureLostTime >= gestureJitterGracePeriod)
                {
                    ResetGesture();
                }
                else
                {
                    _debugInfo = $"[떨림 보정 중...] 잠시 손을 놓쳤습니다 ({gestureLostTime:F2}s / {gestureJitterGracePeriod}s)";
                }
            }
            else
            {
                ResetGesture();
            }
        }

        private void ResetGesture()
        {
            if (isGestureActive)
            {
                isGestureActive = false;
                OnSquareGestureLost?.Invoke();
            }
            currentHoldTime = 0f;
            gestureLostTime = 0f;

            if (frameLineRenderer != null) frameLineRenderer.enabled = false;
            if (_captureManager != null) _captureManager.HidePreview();
        }

        // 손가락이 펴졌는지(직선거리 기준) 판단하는 도우미 함수 
        private bool IsFingerExtended(Mediapipe.Tasks.Components.Containers.NormalizedLandmarks hand, int mcpIdx, int tipIdx, int wristIdx)
        {
            Vector2 wrist = ToScreenPoint(hand.landmarks[wristIdx]);
            Vector2 mcp = ToScreenPoint(hand.landmarks[mcpIdx]);
            Vector2 tip = ToScreenPoint(hand.landmarks[tipIdx]);
            return Vector2.Distance(wrist, tip) > Vector2.Distance(wrist, mcp) * 1.25f; // MCP점보다 끝점이 훨씬 멀어야 펴진 것으로 간주
        }

        private bool TryDetectFrameGesture(Mediapipe.Tasks.Components.Containers.NormalizedLandmarks hand1, 
                                           Mediapipe.Tasks.Components.Containers.NormalizedLandmarks hand2, out Vector2[] quad)
        {
            quad = null;

            // 1. 손가락 펴짐 상태 확인 (엄지 4/2, 검지 8/5, 손목 0)
            bool h1tEx = IsFingerExtended(hand1, 2, 4, 0);
            bool h1iEx = IsFingerExtended(hand1, 5, 8, 0);
            bool h2tEx = IsFingerExtended(hand2, 2, 4, 0);
            bool h2iEx = IsFingerExtended(hand2, 5, 8, 0);

            string dbg = $"[손 상태 디버그]\n";
            dbg += $"H1 엄지 폄:{h1tEx} 검지 폄:{h1iEx}\n";
            dbg += $"H2 엄지 폄:{h2tEx} 검지 폄:{h2iEx}\n";

            if (!h1tEx || !h1iEx || !h2tEx || !h2iEx)
            {
                _debugInfo = dbg + "=> 실패: 엄지와 검지를 모두 쭉 펴주세요!";
                return false;
            }

            // MediaPipe Normalize 좌표를 화면 픽셀 좌표(Unity Screen)로 변환
            // MediaPipe (0,0)은 좌상단, Unity Screen (0,0)은 좌하단
            Vector2 h1tBase = ToScreenPoint(hand1.landmarks[2]); // Thumb MCP
            Vector2 h1tTip = ToScreenPoint(hand1.landmarks[4]); // Thumb Tip
            Vector2 h1iBase = ToScreenPoint(hand1.landmarks[5]); // Index MCP
            Vector2 h1iTip = ToScreenPoint(hand1.landmarks[8]); // Index Tip

            Vector2 h2tBase = ToScreenPoint(hand2.landmarks[2]);
            Vector2 h2tTip = ToScreenPoint(hand2.landmarks[4]);
            Vector2 h2iBase = ToScreenPoint(hand2.landmarks[5]);
            Vector2 h2iTip = ToScreenPoint(hand2.landmarks[8]);

            // 엄지, 검지의 방향 벡터
            Vector2 h1tDir = (h1tTip - h1tBase).normalized;
            Vector2 h1iDir = (h1iTip - h1iBase).normalized;
            Vector2 h2tDir = (h2tTip - h2tBase).normalized;
            Vector2 h2iDir = (h2iTip - h2iBase).normalized;

            // 1. 직각 조건 체크: 평면상의 방향 벡터가 수직인지 확인 (내적이 0에 가까워야 함)
            float dot1 = Vector2.Dot(h1tDir, h1iDir);
            float dot2 = Vector2.Dot(h2tDir, h2iDir);
            
            dbg += $"\n직각 검사 (0에 가까워야함, 기준 {angleTolerance})\n";
            dbg += $"H1(엄지-검지): {Mathf.Abs(dot1):F2}\n";
            dbg += $"H2(엄지-검지): {Mathf.Abs(dot2):F2}\n";

            if (Mathf.Abs(dot1) > angleTolerance || Mathf.Abs(dot2) > angleTolerance)
            {
                _debugInfo = dbg + "=> 실패: 검지와 엄지를 90도로 만들어주세요!";
                return false;
            }

            // 2. 평행 조건 체크: 
            float parA_t = Mathf.Abs(Vector2.Dot(h1tDir, h2tDir)); // 엄지 끼리 평행도 (1에 가까울수록 평행)
            float parA_i = Mathf.Abs(Vector2.Dot(h1iDir, h2iDir)); // 검지 끼리 평행도
            
            float parB_ti = Mathf.Abs(Vector2.Dot(h1tDir, h2iDir)); // H1엄지-H2검지 평행도
            float parB_it = Mathf.Abs(Vector2.Dot(h1iDir, h2tDir)); // H1검지-H2엄지 평행도

            bool isCaseA = parA_t > (1f - angleTolerance) && parA_i > (1f - angleTolerance);
            bool isCaseB = parB_ti > (1f - angleTolerance) && parB_it > (1f - angleTolerance);

            dbg += $"\n평행 검사 (1에 가까워야함, 기준 {1f - angleTolerance:F2})\n";
            dbg += $"유형 A(감독): 엄지끼리 {parA_t:F2}, 검지끼리 {parA_i:F2}\n";
            dbg += $"유형 B(나히다): 교차평행 {parB_ti:F2}, {parB_it:F2}\n";

            if (!isCaseA && !isCaseB)
            {
                _debugInfo = dbg + "=> 실패: 손가락을 서로 평행하게 맞춰주세요!";
                return false;
            }

            // 3. 직선 4개의 교차점 4개 구하기
            // - C1: H1 Thumb ∩ H1 Index (H1의 엄지 뿌리와 검지 뿌리 근처, 즉 손 자체)
            // - C2: H2 Thumb ∩ H2 Index (H2 손 자체)
            // - C3: H1 Thumb ∩ H2 Index (손가락 교차점)
            // - C4: H1 Index ∩ H2 Thumb (손가락 교차점)
            // 경우 B인 경우는 C3: H1 Thumb ∩ H2 Thumb 교차, C4: H1 Index ∩ H2 Index 교차

            Vector2 c1, c2, c3, c4;
            float t1, u1, t2, u2, t3, u3, t4, u4;

            // H1 내부 교차 (손 자체)
            LineIntersection(h1tBase, h1tTip, h1iBase, h1iTip, out c1, out t1, out u1);
            // H2 내부 교차 (손 자체)
            LineIntersection(h2tBase, h2tTip, h2iBase, h2iTip, out c2, out t2, out u2);

            if (isCaseA)
            {
                // 엄지1-검지2 교차
                LineIntersection(h1tBase, h1tTip, h2iBase, h2iTip, out c3, out t3, out u3);
                // 검지1-엄지2 교차
                LineIntersection(h1iBase, h1iTip, h2tBase, h2tTip, out c4, out t4, out u4);
            }
            else // isCaseB
            {
                // 엄지1-엄지2 교차
                LineIntersection(h1tBase, h1tTip, h2tBase, h2tTip, out c3, out t3, out u3);
                // 검지1-검지2 교차
                LineIntersection(h1iBase, h1iTip, h2iBase, h2iTip, out c4, out t4, out u4);
            }

            // 4. 교차점이 손가락 '선분' 또는 '연장선' 위에 있는지 확인 
            // - t > 0 이면 손가락 끝쪽(앞)으로 연장된 선
            // - t < 0 이면 손가락 뿌리쪽(뒤)으로 연장된 선
            float minT = intersectionBackwardLimit;
            float maxT = intersectionForwardLimit;

            dbg += $"\n교차 판정 (연장선 허용: {minT} ~ {maxT})\n";
            dbg += $"t3: {t3:F1}, u3: {u3:F1} | t4: {t4:F1}, u4: {u4:F1}\n";

            if (!IsBetween(t3, minT, maxT) || !IsBetween(u3, minT, maxT) ||
                !IsBetween(t4, minT, maxT) || !IsBetween(u4, minT, maxT))
            {
                _debugInfo = dbg + "=> 실패: 손가락의 앞으로 나아가는 연장선에서 만나지 않았습니다! (손을 너무 엉뚱한 방향으로 가리킴)";
                return false;
            }

            // 5-1. Quad 찌그러짐 방지: 4개의 점을 '완벽한 직사각형'으로 정형화 (Best-Fit Rectangle)
            Vector2 center = (c1 + c2 + c3 + c4) / 4f;

            // 상하 좌우 변의 평균적인 방향과 길이 추출
            Vector2 avgX = ((c3 - c1) + (c2 - c4)) / 2f;
            Vector2 avgY = ((c4 - c1) + (c2 - c3)) / 2f;

            float rectW = avgX.magnitude;
            float rectH = avgY.magnitude;

            if (rectW < 50f || rectH < 50f)
            {
                _debugInfo = dbg + $"=> 실패: 사각형이 너무 작습니다 ({rectW:F0}x{rectH:F0})";
                return false;
            }

            // 완전한 직각을 이루도록 축 강제 정렬
            Vector2 dirX = avgX.normalized;
            Vector2 dirY = new Vector2(-dirX.y, dirX.x); // X축에서 90도 회전
            if (Vector2.Dot(avgY, dirY) < 0) dirY = -dirY; // 올바른 위쪽 방향 맞추기

            Vector2 halfW = dirX * (rectW / 2f);
            Vector2 halfH = dirY * (rectH / 2f);

            // 완벽한 직사각형의 4개 모서리 생성
            Vector2 r1 = center - halfW - halfH; // Bottom-Left
            Vector2 r3 = center + halfW - halfH; // Bottom-Right
            Vector2 r2 = center + halfW + halfH; // Top-Right
            Vector2 r4 = center - halfW + halfH; // Top-Left

            quad = new Vector2[] { r1, r3, r2, r4 };

            _debugInfo = dbg + $"\n[성공!] {currentHoldTime:F2}초째 유지 중...\n사각형 크기: {rectW:F0}x{rectH:F0}";

            // 화면상에 디버그 라인 그리기 (Scene 뷰용)
            Debug.DrawLine(new Vector3(c1.x, c1.y, 0), new Vector3(c3.x, c3.y, 0), Color.green);
            Debug.DrawLine(new Vector3(c3.x, c3.y, 0), new Vector3(c2.x, c2.y, 0), Color.green);
            Debug.DrawLine(new Vector3(c2.x, c2.y, 0), new Vector3(c4.x, c4.y, 0), Color.green);
            Debug.DrawLine(new Vector3(c4.x, c4.y, 0), new Vector3(c1.x, c1.y, 0), Color.green);

            return true;
        }

        private Vector2 ToScreenPoint(Mediapipe.Tasks.Components.Containers.NormalizedLandmark lm)
        {
            return new Vector2(lm.x * Screen.width, (1f - lm.y) * Screen.height);
        }

        private bool IsBetween(float val, float min, float max)
        {
            return val >= min && val <= max;
        }

        // 두 선분(점 A1-A2, B1-B2)을 무한한 직선으로 간주하고 교차점 intersection을 구함
        // t는 첫번째 직선에서의 비율 (0=A1, 1=A2)
        // u는 두번째 직선에서의 비율 (0=B1, 1=B2)
        private bool LineIntersection(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4, 
                                      out Vector2 intersection, out float t, out float u)
        {
            intersection = Vector2.zero;
            t = 0; u = 0;

            float denom = (p1.x - p2.x) * (p3.y - p4.y) - (p1.y - p2.y) * (p3.x - p4.x);
            if (Mathf.Abs(denom) < 0.001f) return false; // 평행함

            t = ((p1.x - p3.x) * (p3.y - p4.y) - (p1.y - p3.y) * (p3.x - p4.x)) / denom;
            u = ((p1.x - p3.x) * (p1.y - p2.y) - (p1.y - p3.y) * (p1.x - p2.x)) / denom;

            intersection = new Vector2(p1.x + t * (p2.x - p1.x), p1.y + t * (p2.y - p1.y));
            return true;
        }
    }
}
