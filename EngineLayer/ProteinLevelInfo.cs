﻿using System.Collections.Generic;
using System.Linq;

namespace EngineLayer
{
    public class ProteinLinkedInfo
    {
        #region Public Constructors

        public ProteinLinkedInfo(HashSet<PeptideWithSetModifications> hashSet)
        {
            PeptidesWithSetModifications = hashSet;
            IsDecoy = PeptidesWithSetModifications.Any(bb => bb.Protein.IsDecoy);
        }
        public ProteinLinkedInfo(HashSet<string> hashSet)
        {
            PeptidesWithSetModificationsString = hashSet;
        }

        #endregion Public Constructors

        #region Public Properties

        public HashSet<PeptideWithSetModifications> PeptidesWithSetModifications { get; }
        public HashSet<string> PeptidesWithSetModificationsString { get; }
        public bool IsDecoy { get; }

        #endregion Public Properties
    }
}