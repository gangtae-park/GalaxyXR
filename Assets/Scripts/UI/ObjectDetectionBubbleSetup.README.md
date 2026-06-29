# Object Detection Bubble UI Setup

1. Open Unity.
2. Wait until script compilation finishes.
3. Click `Tools/Object UI/Setup Detection Bubbles`.
4. Check `UIManager > ObjectUiRequestManager` has `Bubble Spawner` assigned.
5. Check `ObjectDetectionBubbleSpawner` has `Bubble Prefab`, `Bubble Root`, `Radial Menu Spawner`, and `Reference Camera` assigned.
6. Enter Play Mode and run an Object UI YOLO request.
7. Confirm multiple object bubbles appear over detected objects.
8. Click a bubble and confirm the radial menu opens for that object.

Expected logs:

```text
[OBJECT_UI] YOLO detections received: 5
[OBJECT_BUBBLE] spawned index=0 label=...
[OBJECT_BUBBLE] spawned index=1 label=...
[OBJECT_UI] spawned object bubbles count=...
[OBJECT_BUBBLE] clicked label=...
[OBJECT_BUBBLE] calling radial menu spawner...
```
