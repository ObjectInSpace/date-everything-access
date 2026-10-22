"""List the bundles the house scene bundle depends on, so a rip can load only those.

The full game does not fit in memory for AssetRipper; the house scene needs 22 bundles
(2026-10-21 build, saved in rip_scene_bundles.json). Each serialized file inside the scene
bundle names its dependencies as CAB files ("externals"); this maps every CAB name to the
bundle that holds it.

Usage:
  python -I scripts/rip_scene_bundles.py "<game>\Date Everything_Data\StreamingAssets\aa\StandaloneWindows64" scripts/rip_scene_bundles.json
"""
import sys, os, glob, json, time, UnityPy
A=sys.argv[1]; t=time.time()
cab2bundle={}
for p in glob.glob(os.path.join(A,'*.bundle')):
    try:
        env=UnityPy.load(p)
        for b in env.files.values():
            for name in getattr(b,'files',{}): cab2bundle[name.split('.')[0]]=os.path.basename(p)
    except Exception as e: print('skip',os.path.basename(p),e)
print('indexed',len(cab2bundle),'CAB names in',round(time.time()-t),'s')
scene=UnityPy.load(os.path.join(A,'base_scene_thirdpersongreybox_scenes_all.bundle'))
need=set(); other=set()
for b in scene.files.values():
    for sf in getattr(b,'files',{}).values():
        for e in getattr(sf,'externals',[]) or []:
            cab=e.path.split('/')[-1].split('.')[0]
            (need.add(cab2bundle[cab]) if cab in cab2bundle else other.add(e.path))
need.add('base_scene_thirdpersongreybox_scenes_all.bundle')
tot=sum(os.path.getsize(os.path.join(A,n)) for n in need)
print(len(need),'bundles, {:.2f} GB'.format(tot/1e9)); print('\n'.join(sorted(need))); print('not bundles:',sorted(other))
json.dump(sorted(need),open(sys.argv[2],'w'))
