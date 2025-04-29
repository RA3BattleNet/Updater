# Updater

文件增量更新器

## 参考

- https://github.com/RA3BattleNet/Metadata
- https://github.com/RA3BattleNet/Metadata/blob/csharp-linq/Metadata/apps/ra3battlenet/manifests/1.5.2.0.xml
- https://github.com/sisong/HDiffPatch

## 差异patch工具性能测试

|filename|SIZE|bsdiff|deltaq(bsdiff)|xdelta3|hdiffpatch|
|:-:|:-:|:-:|:-:|:-:|:-:|
|EnhancerCorona.old.dll</br>-></br>EnhancerCorona.new.dll|3.3M</br>-></br>3.3M|**483K**|**483K**|576K~632K|921K|
|ubuntu-20.04.2-live-server-amd64.iso</br>-></br>ubuntu-20.04.3-live-server-amd64.iso|1.2G</br>-></br>1.2G|耗时过长|耗时过长|1.1G|**695M**|
|amdvlk32.dll</br>-></br>amdvlk64.dll|102M</br>-></br>115M|**23M**|**23M**|24M|63M|

**结论：**

小文件：bsdiff

大文件：hdiffpatch

## 初步设计框架

![设计框架图](./image/04efb197-3b09-4bec-a50a-0679abd8f28b.png)