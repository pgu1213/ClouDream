from pathlib import Path
import re
roots = [Path('Assets/LostSkiesClouds'), Path('Assets/CloudSea')]
for root in roots:
    for path in root.rglob('*.cs'):
        text = path.read_text(encoding='utf-8-sig')
        text = text.replace('複数 카메라', '여러 카메라').replace('既存 프로필', '기존 프로필').replace('検証 조건', '검증 조건')
        lines = text.splitlines()
        result = []
        field_count = 0
        for line in lines:
            # 필드 묶음은 길어지지 않게 나누고 Inspector 헤더 앞을 비웁니다.
            is_field = bool(re.match(r'        (?:(?:public|private|protected|internal|readonly|static|const)\s+)+[^(){}]+;\s*$', line))
            if line.startswith('        [Header(') or line.startswith('        //'):
                if result and result[-1].strip(): result.append('')
                field_count = 0
            if is_field:
                field_count += 1
                result.append(line)
                if field_count >= 4:
                    result.append('')
                    field_count = 0
            else:
                result.append(line)
                if line.strip() == '' or line.startswith('        ///'):
                    field_count = 0
        text = '\n'.join(result) + '\n'
        text = re.sub(r'\n{3,}', '\n\n', text)
        path.write_text(text, encoding='utf-8')
