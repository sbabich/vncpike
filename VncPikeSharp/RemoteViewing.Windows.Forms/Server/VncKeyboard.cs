#if !NETSTANDARD2_0 && !NETCOREAPP2_1
using RemoteViewing.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RemoteViewing.Vnc
{
    /// <summary>
    /// This class can be used to add Windows keyboard functionality to the VNC server part.
    /// </summary>
    public class VncKeyboard
    {
        public VncKeyboard()
        { }

        /// <summary>
        /// Callback function for keyboard updates.
        /// </summary>
        /// <param name="sender">Sender</param>
        /// <param name="e">EventArgs</param>
        public void OnKeyboardUpdate(object sender, KeyChangedEventArgs e)
        {
            Robot.KeyEvent(e.Pressed, Convert.ToInt32(e.Keysym));
        }
    }
}
#endif
