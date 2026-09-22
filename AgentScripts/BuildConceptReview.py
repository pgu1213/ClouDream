"""실제 Concept V2 Unity 캡처로 검토 자료를 만든다. V1 파일과 원본 PNG를 변경하지 않는다."""
from pathlib import Path
import json
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1] / "Screenshots" / "ConceptMotion-Final"


def build_review():
    """완료된 경로만 120프레임 애니메이션과 시점 비교표에 포함한다."""
    ROOT.mkdir(parents=True, exist_ok=True)
    sequences = []
    expected_names = [f"frame-{index:03}.png" for index in range(120)]
    for folder in sorted(ROOT.iterdir()):
        if not folder.is_dir():
            continue
        files = sorted(folder.glob("frame-*.png"))
        if [file.name for file in files] != expected_names:
            print("아직 완료되지 않은 경로:", folder.name, len(files), "/ 120")
            continue
        frames = []
        for file in files:
            with Image.open(file) as source:
                frames.append(source.convert("RGB"))
        size = frames[0].size
        for frame in frames:
            if frame.size != size:
                raise ValueError(f"프레임 해상도가 서로 다릅니다: {folder.name}")
        frames[0].save(ROOT / (folder.name + ".webp"), save_all=True,
                       append_images=frames[1:], duration=67, loop=0, quality=86, method=4)
        sheet = Image.new("RGB", (1280, 800), "#142635")
        draw = ImageDraw.Draw(sheet)
        for index in range(16):
            frame_index = round(index * 119 / 15)
            tile = frames[frame_index].resize((320, 180))
            x, y = (index % 4) * 320, (index // 4) * 200
            sheet.paste(tile, (x, y))
            draw.text((x + 8, y + 183), f"{folder.name} / frame {frame_index:03}", fill="white")
        sheet.save(ROOT / (folder.name + "-contact.jpg"), quality=94)
        sequences.append({"name": folder.name, "frames": len(files), "width": size[0], "height": size[1]})
        for frame in frames:
            frame.close()
        print(folder.name, len(files))

    (ROOT / "index.json").write_text(json.dumps(sequences, indent=2), encoding="utf-8")
    html = '''<!doctype html><html lang="ko"><meta charset="utf-8">
<title>ClouDream · Concept V2 구름 검토</title>
<style>body{margin:0;background:#102535;color:#edf7fb;font:16px system-ui}main{max-width:1280px;margin:auto;padding:24px}h1{font-size:26px}p{color:#bdcfd8}img{width:100%;aspect-ratio:16/9;object-fit:contain;background:#071b29;border-radius:12px}nav{display:flex;gap:16px;align-items:center;margin:18px 0}select,button{font:inherit;padding:9px;background:#254557;color:white;border:1px solid #4d7188;border-radius:7px}input{flex:1}output{min-width:90px}</style>
<main><h1>Concept V2 · 연속 시점 검토</h1>
<p>각 경로는 Unity 게임 카메라로 촬영한 120개 시점입니다. 공전, 내부 통과, 운해 하강, 층운의 위·옆·아래를 같은 시간대와 다른 시간대에서 비교하세요.</p>
<nav><select id="sequence"></select><button id="play">일시 정지</button><input id="frame" type="range" min="0" max="119" value="0"><output id="count"></output></nav>
<img id="view"><p id="resolution"></p><p>15fps 검토용 재생입니다. 오프라인 캡처이므로 게임 실행 FPS나 모든 프레임의 실시간 검토 완료를 뜻하지 않습니다. 시간과 풍속은 형태 비교를 위해 고정했습니다. <a id="manifest">poses.json 입력 기록</a></p></main>
<script>
const sequences = SEQUENCES;
const select = document.getElementById('sequence');
const slider = document.getElementById('frame');
const view = document.getElementById('view');
const count = document.getElementById('count');
const button = document.getElementById('play');
const resolution = document.getElementById('resolution');
const manifest = document.getElementById('manifest');
let playing = true;
let current = 0;
for (const entry of sequences) { const option = new Option(entry.name, entry.name); select.add(option); }
function showFrame() {
    if (!sequences.length) { count.textContent = '촬영 대기'; resolution.textContent = '완료된 120프레임 경로가 아직 없습니다.'; return; }
    view.src = select.value + '/frame-' + String(current).padStart(3, '0') + '.png';
    slider.value = current;
    count.textContent = (current + 1) + ' / 120';
    const entry = sequences[select.selectedIndex];
    resolution.textContent = entry.width + ' × ' + entry.height + ' / 원본 PNG 프레임';
    manifest.href = select.value + '/poses.json';
}
function preload() { if (!sequences.length) { return; } for (let i=0;i<120;i++) { const img = new Image(); img.src = select.value + '/frame-' + String(i).padStart(3, '0') + '.png'; } }
select.onchange = function() { current=0; preload(); showFrame(); };
slider.oninput = function() { current=Number(slider.value); playing=false; button.textContent='재생'; showFrame(); };
button.onclick = function() { playing=!playing; button.textContent='재생'; if (playing) { button.textContent='일시 정지'; } };
setInterval(function() { if (playing && sequences.length) { current=(current+1)%120; showFrame(); } }, 67);
preload(); showFrame();
</script></html>'''
    html = html.replace("SEQUENCES", json.dumps(sequences))
    (ROOT / "review.html").write_text(html, encoding="utf-8")


if __name__ == "__main__":
    build_review()
