# CLAUDE.md

Real-time face recognition of politicians from NDI video (vMix). C# / .NET, ONNX Runtime on CPU. See README.md.

## Commands
- `dotnet build TVFaceId.sln`
- `dotnet run --project Tools/DatabaseBuilder -- --output Web/faces --models Web/Models` — builds the face database
- `dotnet run --project Web` — web UI + recognition worker (SignalR)
- Release: `dotnet publish Web/Web.csproj -c Release -r win-x64 --self-contained true -o publish`

## Layout
- `Core`: `FaceDetector` (SCRFD-500M), `FaceRecognizer` (ArcFace w600k_mbf), `FaceDatabase`, image loading
- `Web`: ASP.NET Core host, `NdiManager` (NDI sources), `RecognitionWorker` (background service), `RecognitionHub`
- `NDI`: console worker for headless monitoring
- `Tools/DatabaseBuilder`: scrapes GOV.UK / Wikipedia / Wikimedia Commons and validates photos with the recognizer

## Rules
- Inference stays on CPU — the GPU belongs to vMix
- Windows only (System.Drawing, NDI native library); do not add Linux-only code paths without asking
- Never commit models (`*.onnx`), `faces/` or NDI runtime DLLs — they are third-party and non-redistributable
- NDI receivers are shared between threads: guard start/stop in `NdiManager` (a race condition there was fixed with a grace period — keep it)
- Recognition threshold is 0.71 cosine similarity; change it only with an evaluation run
