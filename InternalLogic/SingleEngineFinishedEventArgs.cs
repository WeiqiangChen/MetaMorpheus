﻿using System;

namespace InternalLogicEngineLayer
{
    public class SingleEngineFinishedEventArgs : EventArgs
    {
        private readonly MyResults myResults;

        public SingleEngineFinishedEventArgs(MyResults myResults)
        {
            this.myResults = myResults;
        }

        public override string ToString()
        {
            return myResults.ToString();
        }
    }
}