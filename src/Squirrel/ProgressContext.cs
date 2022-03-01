using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Squirrel
{
    public class ProgressContext
    {
        public event Action<int> OnProgressChanged;

        public double IncreamentSize { get; set; }
        public double Current { get; set; }

        object _lock = new();

        public ProgressContext(Action<int> onProgressChanged, double increment)
        {
            OnProgressChanged = onProgressChanged;
            IncreamentSize = increment;
        }

        public void Increament()
        {
            Increament(IncreamentSize);
        }

        public void Increament(double increment)
        {
            lock (_lock) {
                Current += increment;
                OnProgressChanged((int)Math.Round(Current));
            }
        }
    }
}
