﻿using MassSpectrometry;
using MzLibUtil;
using Proteomics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EngineLayer.ClassicSearch
{
    public class ClassicSearchEngine : MetaMorpheusEngine
    {
        #region Private Fields

        private readonly MassDiffAcceptor searchModes;

        private readonly List<Protein> proteinList;

        private readonly List<ModificationWithMass> fixedModifications;

        private readonly List<ModificationWithMass> variableModifications;

        private readonly Psm[] globalPsms;

        private readonly Ms2ScanWithSpecificMass[] arrayOfSortedMS2Scans;

        private readonly double[] myScanPrecursorMasses;

        private readonly List<ProductType> lp;

        private readonly bool addCompIons;

        private readonly CommonParameters commonParameters;

        private readonly List<DissociationType> dissociationTypes;

        #endregion Private Fields

        #region Public Constructors

        public ClassicSearchEngine(Psm[] globalPsms, Ms2ScanWithSpecificMass[] arrayOfSortedMS2Scans, List<ModificationWithMass> variableModifications, List<ModificationWithMass> fixedModifications, List<Protein> proteinList, List<ProductType> lp, MassDiffAcceptor searchModes, bool addCompIons, CommonParameters CommonParameters, List<string> nestedIds) : base(nestedIds)
        {
            this.globalPsms = globalPsms;
            this.arrayOfSortedMS2Scans = arrayOfSortedMS2Scans;
            this.myScanPrecursorMasses = arrayOfSortedMS2Scans.Select(b => b.PrecursorMass).ToArray();
            this.variableModifications = variableModifications;
            this.fixedModifications = fixedModifications;
            this.proteinList = proteinList;
            this.searchModes = searchModes;
            this.lp = lp;
            this.addCompIons = addCompIons;
            this.dissociationTypes = DetermineDissociationType(lp);
            this.commonParameters = CommonParameters;
        }

        #endregion Public Constructors

        #region Protected Methods

        protected override MetaMorpheusEngineResults RunSpecific()
        {
            Status("In classic search engine!");

            int totalProteins = proteinList.Count;

            var observedPeptides = new HashSet<CompactPeptide>();

            Status("Getting ms2 scans...");

            HashSet<string> seenSequences = new HashSet<string>();
            var lockObject = new object();
            int proteinsSeen = 0;
            int old_progress = 0;
            TerminusType terminusType = ProductTypeMethod.IdentifyTerminusType(lp);
            Status("Starting classic search loop...");
            Parallel.ForEach(Partitioner.Create(0, totalProteins), partitionRange =>
            {
                var psms = new Psm[arrayOfSortedMS2Scans.Length];
                for (int i = partitionRange.Item1; i < partitionRange.Item2; i++)
                {
                    Protein protein = proteinList[i];
                    List<string> digestedList = protein.DigestHeck().ToList();
                    foreach (var yyy in digestedList)
                    {
                        lock (seenSequences)
                        {
                            if (seenSequences.Contains(yyy))
                                continue;
                            seenSequences.Add(yyy);
                        }
                        var correspondingCompactPeptide = new CompactPeptide(yyy, terminusType);
                        if (!commonParameters.ConserveMemory)
                        {
                            var peptideWasObserved = observedPeptides.Contains(correspondingCompactPeptide);
                            if (peptideWasObserved)
                                continue;
                            lock (observedPeptides)
                            {
                                peptideWasObserved = observedPeptides.Contains(correspondingCompactPeptide);
                                if (peptideWasObserved)
                                    continue;
                                observedPeptides.Add(correspondingCompactPeptide);
                            }
                        }

                        var productMasses = correspondingCompactPeptide.ProductMassesMightHaveDuplicatesAndNaNs(lp);
                        Array.Sort(productMasses);

                        var searchMode = searchModes;
                        foreach (ScanWithIndexAndNotchInfo scanWithIndexAndNotchInfo in GetAcceptableScans(correspondingCompactPeptide.MonoisotopicMassIncludingFixedMods, searchMode).ToList())
                        {
                            double thePrecursorMass = scanWithIndexAndNotchInfo.theScan.PrecursorMass;
                            var score = CalculatePeptideScore(scanWithIndexAndNotchInfo.theScan.TheScan, commonParameters.ProductMassTolerance, productMasses, thePrecursorMass, dissociationTypes, addCompIons);

                            Psm currentPsm=psms[scanWithIndexAndNotchInfo.scanIndex];
                            if (currentPsm == null)
                            {
                                psms[scanWithIndexAndNotchInfo.scanIndex] = new Psm(correspondingCompactPeptide, scanWithIndexAndNotchInfo.notch, score, scanWithIndexAndNotchInfo.scanIndex, scanWithIndexAndNotchInfo.theScan, commonParameters.ExcelCompatible);
                            }
                            else
                            {
                                currentPsm.AddOrReplace(correspondingCompactPeptide, score, scanWithIndexAndNotchInfo.notch, commonParameters.ReportAllAmbiguity);
                                int scoreInt = (int)score;
                                while (currentPsm.allScores.Count < scoreInt)
                                    currentPsm.allScores.Add(0);
                                currentPsm.allScores[scoreInt]++;
                            }
                        }
                    }                    
                }
                lock (lockObject)
                {
                    for (int i = 0; i < globalPsms.Length; i++)
                        if (psms[i] != null)
                        {
                            Psm oldPsm = globalPsms[i];
                            if (oldPsm == null)
                                globalPsms[i] = psms[i];
                            else
                            {
                                Psm newPsm = psms[i];
                                List<int> sumList = new List<int>();
                                foreach (int bin in oldPsm.allScores)
                                    sumList.Add(bin);
                                while (sumList.Count < newPsm.allScores.Count)
                                    sumList.Add(0);
                                for (int j = 0; j < newPsm.allScores.Count; j++)
                                    sumList[j] += newPsm.allScores[j];
                                oldPsm.AddOrReplace(newPsm, commonParameters.ReportAllAmbiguity);
                            }
                        }
                    proteinsSeen += partitionRange.Item2 - partitionRange.Item1;
                    var new_progress = (int)((double)proteinsSeen / (totalProteins) * 100);
                    if (new_progress > old_progress)
                    {
                        ReportProgress(new ProgressEventArgs(new_progress, "In classic search loop", nestedIds));
                        old_progress = new_progress;
                    }
                }
            });
            return new MetaMorpheusEngineResults(this);
        }

        #endregion Protected Methods

        #region Private Methods

        private IEnumerable<ScanWithIndexAndNotchInfo> GetAcceptableScans(double peptideMonoisotopicMass, MassDiffAcceptor searchMode)
        {
            foreach (AllowedIntervalWithNotch allowedIntervalWithNotch in searchMode.GetAllowedPrecursorMassIntervals(peptideMonoisotopicMass).ToList())
            {
                DoubleRange allowedInterval = allowedIntervalWithNotch.allowedInterval;
                int scanIndex = GetFirstScanWithMassOverOrEqual(allowedInterval.Minimum);
                if (scanIndex < arrayOfSortedMS2Scans.Length)
                {
                    var scanMass = myScanPrecursorMasses[scanIndex];
                    while (scanMass <= allowedInterval.Maximum)
                    {
                        var theScan = arrayOfSortedMS2Scans[scanIndex];
                        yield return new ScanWithIndexAndNotchInfo(theScan, allowedIntervalWithNotch.notch, scanIndex);
                        scanIndex++;
                        if (scanIndex == arrayOfSortedMS2Scans.Length)
                            break;
                        scanMass = myScanPrecursorMasses[scanIndex];
                    }
                }
            }
        }

        private int GetFirstScanWithMassOverOrEqual(double minimum)
        {
            int index = Array.BinarySearch(myScanPrecursorMasses, minimum);
            if (index < 0)
                index = ~index;

            // index of the first element that is larger than value
            return index;
        }

        #endregion Private Methods
    }
}