﻿using Propro.Constants;
using Propro.Domains;
using Propro.Structs;
using pwiz.CLI.cv;
using pwiz.CLI.msdata;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Propro_Client.Domains.Aird;


namespace Propro.Logics
{
    internal class DDAConverter : IConverter
    {
        private int totalSize;
        private int progress;
        List<TempIndex> ms1List = new List<TempIndex>();
        Hashtable ms2Table = new Hashtable();

        public DDAConverter(ConvertJobInfo jobInfo) : base(jobInfo) {}

        public override void doConvert()
        {
            start();
            initDirectory();
            using (airdStream = new FileStream(jobInfo.airdFilePath, FileMode.Create))
            {
                using (airdJsonStream = new FileStream(jobInfo.airdJsonFilePath, FileMode.Create))
                {
                    readVendorFile();//准备读取Vendor文件
                    initGlobalVar();//初始化全局变量
                    preProcess();//MS1和MS2分开建立索引
                    doWithMS1Block();//处理MS1,并将索引写入文件流中
                    doWithMS2Block();//处理MS2,并将索引写入文件流中
                    writeToAirdInfoFile();//将Info数据写入文件
                }
            }
            finish();
        }

        private void initGlobalVar()
        {
            totalSize = spectrumList.size();
            progress = 0;
            jobInfo.log("Total Size(include useless MS1):" + totalSize);
            startPosition = 0;
        }

        private void preProcess()
        {
            int parentNum = 0;
            jobInfo.log("Preprocessing:" + totalSize, "Preprocessing");
            for(var i = 0; i <totalSize; i++)
            {
                //最后一个谱图,单独判断
                if (i == totalSize - 1)
                {
                    if (getMsLevel(i).Equals(MsLevel.MS1)) ms1List.Add(parseMS1(spectrumList.spectrum(i), i));//如果是MS1谱图,加入到MS1List
                    if (getMsLevel(i).Equals(MsLevel.MS2)) addToMS2Map(parseMS2(spectrumList.spectrum(i), i, parentNum)); //如果是MS2谱图,加入到谱图组
                }
                else
                {
                    //如果这个谱图是MS1
                    if (getMsLevel(i).Equals(MsLevel.MS1))
                    {
                        ms1List.Add(parseMS1(spectrumList.spectrum(i), i));//加入MS1List
                        if (getMsLevel(i + 1).Equals(MsLevel.MS2)) parentNum = i;//如果下一个谱图是MS2, 那么将这个谱图设置为当前的父谱图
                    }
                    if (getMsLevel(i).Equals(MsLevel.MS2)) addToMS2Map(parseMS2(spectrumList.spectrum(i), i, parentNum)); //如果这个谱图是MS2
                }
            }

            jobInfo.log("Effective MS1 List Size:" + ms1List.Count);
            jobInfo.log("MS2 Group List Size:" + ms2Table.Count);
            jobInfo.log("Start Processing MS1 List");
        
        }

        private string getMsLevel(int index)
        {
            return spectrumList.spectrum(index).cvParamChild(CVID.MS_ms_level).value.ToString();
        }

        //建立Ms1Scan的索引
        private TempIndex parseMS1(Spectrum spectrum, int index)
        {
            TempIndex ms1 = new TempIndex();
            ms1.level = 1;
            ms1.num = index;
            if (spectrum.scanList.scans.Count != 1)
            {
                return ms1;
            }

            Scan scan = spectrum.scanList.scans[0];
            ms1.rt = parseRT(scan);

            return ms1;
        }

        //建立Ms2Scan的索引
        private TempIndex parseMS2(Spectrum spectrum, int index, int parentIndex)
        {
            TempIndex ms2 = new TempIndex();
            ms2.level = 2;
            ms2.pNum = parentIndex;
            ms2.num = index;
            try
            {
                float mz = (float)double.Parse(spectrum.precursors[0].isolationWindow
                    .cvParamChild(CVID.MS_isolation_window_target_m_z).value.ToString());
                float lowerOffset = (float)double.Parse(spectrum.precursors[0].isolationWindow
                    .cvParamChild(CVID.MS_isolation_window_lower_offset).value.ToString());
                float upperOffset = (float)double.Parse(spectrum.precursors[0].isolationWindow
                    .cvParamChild(CVID.MS_isolation_window_upper_offset).value.ToString());

                ms2.mz = mz;
                ms2.mzStart = mz - lowerOffset;
                ms2.mzEnd = mz + upperOffset;
                ms2.wid = lowerOffset + upperOffset;
            }
            catch (Exception e)
            {
                jobInfo.log("ERROR:SpectrumIndex:" + spectrum.index)
                    .log("ERROR:SpectrumId:" + spectrum.id)
                    .log("ERROR: mz:" + spectrum.precursors[0].isolationWindow
                             .cvParamChild(CVID.MS_isolation_window_target_m_z).value)
                    .log("ERROR: lowerOffset:" + spectrum.precursors[0].isolationWindow
                             .cvParamChild(CVID.MS_isolation_window_lower_offset).value)
                    .log("ERROR: upperOffset:" + spectrum.precursors[0].isolationWindow
                             .cvParamChild(CVID.MS_isolation_window_upper_offset).value);
                throw e;
            }

            if (spectrum.scanList.scans.Count != 1) return ms2;
            Scan scan = spectrum.scanList.scans[0];
            ms2.rt = parseRT(scan);

            return ms2;
        }

        private void addToMS2Map(TempIndex ms2Index)
        {
            if(ms2Table.Contains(ms2Index.pNum))
            {
                (ms2Table[ms2Index.pNum] as List<TempIndex>).Add(ms2Index);
            }
            else
            {
                List<TempIndex> indexList = new List<TempIndex>();
                indexList.Add(ms2Index);
                ms2Table.Add(ms2Index.pNum, indexList);
            }
        }

        //处理MS1
        private void doWithMS1Block()
        {
            BlockIndex blockIndex = new BlockIndex();
            blockIndex.level = 1;
            blockIndex.startPtr = 0;
            for (int i = 0; i < ms1List.Count; i++)
            {
                jobInfo.log(null, "MS1:" + i + "/" + ms1List.Count);
                TempIndex scanIndex = ms1List[i];
                TempScan ts = new TempScan(scanIndex.num, scanIndex.rt);
                compress(spectrumList.spectrum(scanIndex.num, true), ts);
                blockIndex.nums.Add(ts.num);
                blockIndex.rts.Add(ts.rt);
                blockIndex.mzs.Add(ts.mzArrayBytes.Length);
                blockIndex.ints.Add(ts.intArrayBytes.Length);
                startPosition = startPosition + ts.mzArrayBytes.Length + ts.intArrayBytes.Length;
                airdStream.Write(ts.mzArrayBytes, 0, ts.mzArrayBytes.Length);
                airdStream.Write(ts.intArrayBytes, 0, ts.intArrayBytes.Length);
            }
            blockIndex.endPtr = startPosition;
            blockIndexList.Add(blockIndex);

        }

        //处理MS2
        private void doWithMS2Block()
        {
            jobInfo.log("Start Processing MS2 List");
            ArrayList keys = new ArrayList(ms2Table.Keys);
            keys.Sort();
            foreach(int key in keys)
            {
                List<TempIndex> tempIndexList = ms2Table[key] as List<TempIndex>;
                //为每一组key创建一个Block
                BlockIndex blockIndex = new BlockIndex();
                blockIndex.level = 2;
                blockIndex.startPtr = startPosition;
                blockIndex.num = key;
                //创建这一个block中每一个ms2的窗口序列
                List<WindowRange> ms2Ranges = new List<WindowRange>();
                jobInfo.log(null, "MS2:" + progress + "/" + ms2Table.Keys.Count);
                progress++;

                foreach (TempIndex index in tempIndexList)
                {
                    ms2Ranges.Add(new WindowRange(index.mzStart, index.mzEnd, index.mz));
                    TempScan ts = new TempScan(index.num, index.rt);
                    compress(spectrumList.spectrum(index.num, true), ts);
                    blockIndex.nums.Add(ts.num);
                    blockIndex.rts.Add(ts.rt);
                    blockIndex.mzs.Add(ts.mzArrayBytes.Length);
                    blockIndex.ints.Add(ts.intArrayBytes.Length);
                    startPosition = startPosition + ts.mzArrayBytes.Length + ts.intArrayBytes.Length;
                    airdStream.Write(ts.mzArrayBytes, 0, ts.mzArrayBytes.Length);
                    airdStream.Write(ts.intArrayBytes, 0, ts.intArrayBytes.Length);
                }
                blockIndex.rangeList = ms2Ranges;
                blockIndex.endPtr = startPosition;
                blockIndexList.Add(blockIndex);
                jobInfo.log("MS2 Group Finished:" + progress + "/" + ms2Table.Keys.Count);
            } 

        }

  
    }

}
