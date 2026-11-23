# TVFaceId

Real-time recognition of politicians in live TV video. It reads an NDI stream (vMix), detects faces, identifies EU/US/UK leaders and shows their name, title and a Google search link in a browser overlay. Everything runs locally on CPU.

## Why
Live broadcast graphics (lower thirds) are normally typed by hand. I work in live TV production, so this tool solves a problem I see on the job: identify the person on screen and prepare the caption automatically.

## Features
- NDI input, so it works with vMix and any other NDI source
- Face detection (SCRFD-500M) and recognition (ArcFace, w600k_mbf) via ONNX Runtime, CPU only, so it doesn't compete with vMix for the GPU
- Web UI over SignalR: name, role, country, confidence bar, click-through to Google search
- Sleep mode: a cheap 16x16 pixel diff skips inference on static frames
- Automatic face database builder with photo validation (see below)

## Architecture

Projects: `Core` (inference, face database), `Web` (ASP.NET Core + SignalR UI), `NDI` (console worker), `Tools/DatabaseBuilder` (database generator).

## The face database builder
Names come from the GOV.UK ministers API and Wikipedia "current ..." lists, plus a small curated list. Photos come from official portraits, Wikipedia and Wikimedia Commons.
Wrong photos are filtered by the recognizer itself: each candidate image must contain one dominant face, and images that don't match the first (official) portrait are dropped. Each person ends up as `faces/<slug>/person.json` plus several verified photos, and recognition uses all of their embeddings.

## Getting started
Requirements: Windows x64, .NET 10 SDK, NDI Runtime or NDI Tools.

1. Download the models into `Web/Models/`: `det_500m.onnx` and `w600k_mbf.onnx` (both are in InsightFace's `buffalo_sc` pack). They are for non-commercial research use, so they are not included in this repo.
2. Generate the database:
   `dotnet run --project Tools/DatabaseBuilder -- --output Web/faces --models Web/Models`
3. Run: `dotnet run --project Web`, then open the printed URL.
4. Optional, if the NDI source is on another subnet: set the env var `Ndi__ExtraIps`.

Windows build: `dotnet publish Web/Web.csproj -c Release -r win-x64 --self-contained true -o publish`

## Engineering notes
Bugs found and fixed while building it: a double sigmoid in SCRFD post-processing, a wrong anchor offset, an IoU typo in NMS, Wikipedia API rate limiting (fixed with 50-title batches), and a race condition in the NDI receiver (fixed with a grace period).

## Limitations
- Accuracy drops with profile views, heavy motion blur and small faces
- The roster needs periodic refresh as governments change (`DatabaseBuilder` is built for re-runs)
- Windows only (System.Drawing, NDI native library)

## Privacy and ethics
Only public officials are in the database. Video is processed locally and nothing is stored or sent anywhere. Photos are downloaded by the builder from their original sources and are not redistributed here; check each source's license before reusing them.

## License
Code: MIT. The models and photos are separate third-party works under their own terms. NDI® is a registered trademark of Vizrt NDI AB.
