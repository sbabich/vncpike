using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RemoteViewing.Logging
{
    public class ConsoleWriter
    {
        public static void Go(string pre, byte[] buff, int length)
        {
            string msg = pre + ": ";

            if (length < 30)
            {
                for (int i = 0; i < length; i++)
                {
                    msg += buff[i] + " ";
                }
            }
            else
            {
                msg += length.ToString() + " bytes";
            }

            Console.WriteLine(msg);
        }
    }
}
