﻿using Proteomics;
using Proteomics.Fragmentation;
using Proteomics.ProteolyticDigestion;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UsefulProteomicsDatabases;

namespace EngineLayer.Indexing
{
    public class IndexingEngine : MetaMorpheusEngine
    {
        private const int FragmentBinsPerDalton = 1000;
        private readonly List<Protein> ProteinList;

        private readonly List<Modification> FixedModifications;
        private readonly List<Modification> VariableModifications;
        private readonly int CurrentPartition;
        private readonly DecoyType DecoyType;
        private readonly double MaxFragmentSize;
        public readonly bool GeneratePrecursorIndex;

        public IndexingEngine(List<Protein> proteinList, List<Modification> variableModifications, List<Modification> fixedModifications, int currentPartition, DecoyType decoyType, CommonParameters commonParams, double maxFragmentSize, bool generatePrecursorIndex, List<string> nestedIds) : base(commonParams, nestedIds)
        {
            ProteinList = proteinList;
            VariableModifications = variableModifications;
            FixedModifications = fixedModifications;
            CurrentPartition = currentPartition + 1;
            DecoyType = decoyType;
            MaxFragmentSize = maxFragmentSize;
            GeneratePrecursorIndex = generatePrecursorIndex;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Partitions: " + CurrentPartition + "/" + commonParameters.TotalPartitions);
            sb.AppendLine("Precursor Index: " + GeneratePrecursorIndex);
            sb.AppendLine("Search Decoys: " + DecoyType);
            sb.AppendLine("Number of proteins: " + ProteinList.Count);
            sb.AppendLine("Number of fixed mods: " + FixedModifications.Count);
            sb.AppendLine("Number of variable mods: " + VariableModifications.Count);
            sb.AppendLine("Dissociation Type: " + commonParameters.DissociationType);

            sb.AppendLine("protease: " + commonParameters.DigestionParams.Protease);
            sb.AppendLine("initiatorMethionineBehavior: " + commonParameters.DigestionParams.InitiatorMethionineBehavior);
            sb.AppendLine("maximumMissedCleavages: " + commonParameters.DigestionParams.MaxMissedCleavages);
            sb.AppendLine("minPeptideLength: " + commonParameters.DigestionParams.MinPeptideLength);
            sb.AppendLine("maxPeptideLength: " + commonParameters.DigestionParams.MaxPeptideLength);
            sb.AppendLine("maximumVariableModificationIsoforms: " + commonParameters.DigestionParams.MaxModificationIsoforms);
            sb.AppendLine("digestionTerminus: " + commonParameters.DigestionParams.FragmentationTerminus);
            sb.AppendLine("maxModsForEachPeptide: " + commonParameters.DigestionParams.MaxModsForPeptide);
            sb.AppendLine("cleavageSpecificity: " + commonParameters.DigestionParams.SearchModeType);
            sb.AppendLine("specificProtease: " + commonParameters.DigestionParams.SpecificProtease);

            sb.Append("Localizeable mods: " + ProteinList.Select(b => b.OneBasedPossibleLocalizedModifications.Count).Sum());
            return sb.ToString();
        }

        protected override MetaMorpheusEngineResults RunSpecific()
        {
            double progress = 0;
            int oldPercentProgress = 0;

            // digest database
            List<PeptideWithSetModifications> globalPeptides = new List<PeptideWithSetModifications>();

            Parallel.ForEach(Partitioner.Create(0, ProteinList.Count), new ParallelOptions { MaxDegreeOfParallelism = commonParameters.MaxThreadsToUsePerFile }, (range, loopState) =>
            {
                List<PeptideWithSetModifications> localPeptides = new List<PeptideWithSetModifications>();

                for (int i = range.Item1; i < range.Item2; i++)
                {
                    // Stop loop if canceled
                    if (GlobalVariables.StopLoops)
                    {
                        loopState.Stop();
                        return;
                    }


                    localPeptides.AddRange(ProteinList[i].Digest(commonParameters.DigestionParams, FixedModifications, VariableModifications));


                    progress++;
                    var percentProgress = (int)((progress / ProteinList.Count) * 100);

                    if (percentProgress > oldPercentProgress)
                    {
                        oldPercentProgress = percentProgress;
                        ReportProgress(new ProgressEventArgs(percentProgress, "Digesting proteins...", nestedIds));
                    }
                }

                lock (globalPeptides)
                {
                    globalPeptides.AddRange(localPeptides);
                }
            });

            // sort peptides by mass
            var peptidesSortedByMass = globalPeptides.AsParallel().WithDegreeOfParallelism(commonParameters.MaxThreadsToUsePerFile).OrderBy(p => p.MonoisotopicMass).ToList();
            globalPeptides = null;

            // create fragment index
            List<int>[] fragmentIndex;

            try
            {
                fragmentIndex = new List<int>[(int)Math.Ceiling(MaxFragmentSize) * FragmentBinsPerDalton + 1];
            }
            catch (OutOfMemoryException)
            {
                throw new MetaMorpheusException("Max fragment mass too large for indexing engine; try \"Classic Search\" mode, or make the maximum fragment mass smaller");
            }

            // populate fragment index
            progress = 0;
            oldPercentProgress = 0;
            for (int peptideId = 0; peptideId < peptidesSortedByMass.Count; peptideId++)
            {
                var fragmentMasses = peptidesSortedByMass[peptideId].Fragment(commonParameters.DissociationType, commonParameters.DigestionParams.FragmentationTerminus).Select(m => m.NeutralMass).ToList();

                foreach (var theoreticalFragmentMass in fragmentMasses)
                {
                    if (theoreticalFragmentMass < MaxFragmentSize && theoreticalFragmentMass > 0)
                    {
                        int fragmentBin = (int)Math.Round(theoreticalFragmentMass * FragmentBinsPerDalton);

                        if (fragmentIndex[fragmentBin] == null)
                            fragmentIndex[fragmentBin] = new List<int> { peptideId };
                        else
                            fragmentIndex[fragmentBin].Add(peptideId);
                    }
                }

                progress++;
                var percentProgress = (int)((progress / peptidesSortedByMass.Count) * 100);

                if (percentProgress > oldPercentProgress)
                {
                    oldPercentProgress = percentProgress;
                    ReportProgress(new ProgressEventArgs(percentProgress, "Fragmenting peptides...", nestedIds));
                }
            }

            List<int>[] precursorIndex = null;

            if (GeneratePrecursorIndex)
            {
                // create precursor index
                try
                {
                    precursorIndex = new List<int>[(int)Math.Ceiling(MaxFragmentSize) * FragmentBinsPerDalton + 1];
                }
                catch (OutOfMemoryException)
                {
                    throw new MetaMorpheusException("Max precursor mass too large for indexing engine; try \"Classic Search\" mode, or make the maximum fragment mass smaller");
                }
                progress = 0;
                oldPercentProgress = 0;
                ReportProgress(new ProgressEventArgs(0, "Creating precursor index...", nestedIds));

                for (int i = 0; i < peptidesSortedByMass.Count; i++)
                {
                    double mass = peptidesSortedByMass[i].MonoisotopicMass;
                    if (!Double.IsNaN(mass))
                    {
                        if (mass > MaxFragmentSize) //if the precursor is larger than the index allows, then stop adding precursors
                        {
                            break;
                        }

                        int precursorBin = (int)Math.Round(mass * FragmentBinsPerDalton);

                        if (precursorIndex[precursorBin] == null)
                            precursorIndex[precursorBin] = new List<int> { i };
                        else
                            precursorIndex[precursorBin].Add(i);
                    }
                    progress++;
                    var percentProgress = (int)((progress / peptidesSortedByMass.Count) * 100);

                    if (percentProgress > oldPercentProgress)
                    {
                        oldPercentProgress = percentProgress;
                        ReportProgress(new ProgressEventArgs(percentProgress, "Creating precursor index...", nestedIds));
                    }
                }
            }

            return new IndexingResults(peptidesSortedByMass, fragmentIndex, precursorIndex, this);
        }
    }
}