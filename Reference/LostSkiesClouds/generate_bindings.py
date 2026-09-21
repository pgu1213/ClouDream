from pathlib import Path
import json,re,shutil
root=Path('Assets/LostSkiesClouds')
(root/'Runtime').mkdir(exist_ok=True)
(root/'Editor').mkdir(exist_ok=True)
(root/'Presets').mkdir(exist_ok=True)
types=json.loads(Path('Reference/LostSkiesClouds/AllTypeFields.json').read_text(encoding='utf-8-sig'))
names=['Expanse.UniversalCloudLayer/UniversalCloudLayerRenderSettings','Expanse.GlobalRenderSettings','Expanse.DirectionalLightRenderSettings','Expanse.CloudPrimitive','Expanse.CloudLinePrimitive']
cs='// GPU layouts recovered from the supplied Expanse metadata. No original DLL is required.\nusing System;\nusing System.Runtime.InteropServices;\nusing UnityEngine;\nnamespace ClouDream.LostSkies {\n'
mapping={'System.Int32':'int','System.Single':'float','UnityEngine.Vector2':'Vector2','UnityEngine.Vector3':'Vector3','UnityEngine.Vector4':'Vector4','UnityEngine.Color':'Color','UnityEngine.Matrix4x4':'Matrix4x4'}
for n in names:
    t=next(t for t in types if t['name']==n)
    cs+='[Serializable, StructLayout(LayoutKind.Sequential, Pack=4)] public struct '+n.split('/')[-1].split('.')[-1]+' {\n'
    for f in t['fields']:
        if not f['static']:
            cs+='    public '+mapping.get(f['type'],'int')+' '+f['name']+';\n'
    cs+='}\n'
cs+='}\n'
(root/'Runtime'/'OriginalGpuLayouts.cs').write_text(cs)
source=Path(r'C:\Users\pgu51\Desktop\공유\디컴파일\Lost-Skies_Decom\ExportedProject\Assets')
for label in ['normal','herald']:
    raw=(source/'MonoBehaviour'/f'cloud_preset_ms10_{label}_0.asset').read_text()
    # Preserve the original serialized preset as reference text, not a missing-script Unity asset.
    (root/'Presets'/f'Original-{label}.txt').write_text(raw)
    data={}
    for key,val in re.findall(r'^    (\w+): (.+)$',raw.split('\n  renderSettings:\n')[-1],re.M):
        if val.startswith('{'):
            data[key]={k:float(v) for k,v in re.findall(r'(\w+): ([^,}]+)',val)}
        else:
            data[key]=float(val) if any(c in val for c in '.Ee') else int(val)
    (root/'Presets'/f'{label}.json').write_text(json.dumps(data,indent=2))

raw=Path('Reference/LostSkiesClouds/CloudRenderer.Bindings.yaml').read_text()
cbs=raw.split('    constantBuffers:\n')[1].split('    resourcesResolved:')[0]
res=[]
for b in re.split(r'^    - name: ',cbs,flags=re.M)[1:]:
    name=b.splitlines()[0]
    params=[]
    for p in re.split(r'^      - name: ',b,flags=re.M)[1:]:
        vals=dict(re.findall(r'^        (\w+): (\d+)',p,re.M))
        params.append({'name':p.splitlines()[0],**{k:int(v) for k,v in vals.items()}})
    res.append({'name':name,'size':int(re.search(r'byteSize: (\d+)',b)[1]),'params':params})
Path('Reference/LostSkiesClouds/ConstantBuffers.json').write_text(json.dumps(res,indent=2))
for i in [1,2,3]:
    b=res[i]
    print(i,b['name'],b['size'],[(p['name'],p['offset']) for p in b['params']])
