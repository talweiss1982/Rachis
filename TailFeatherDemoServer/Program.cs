using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Owin.Hosting;

namespace TailFeatherDemoServer
{
    class Program
    {
        static void Main(string[] args)
        {
            //TailFeatherCluster.CreateTailFeatherCluster(@"C:\work\tailfeather\", 8090, 5, 8092);
            using (WebApp.Start<TailFeatherHost>("http://localhost:9091/"))
            {
                Console.WriteLine("TailFeather server running on port:9091");
                Console.WriteLine("   _____      _                  _       _____ U _____ u    _       _____    _   _  U _____ u   ____     \r\n |_ \" _| U  /\"\\  u     ___     |\"|     |\" ___|\\| ___\"|/U  /\"\\  u  |_ \" _|  |\'| |\'| \\| ___\"|/U |  _\"\\ u  \r\n   | |    \\/ _ \\/     |_\"_|  U | | u  U| |_  u |  _|\"   \\/ _ \\/     | |   /| |_| |\\ |  _|\"   \\| |_) |/  \r\n  /| |\\   / ___ \\      | |    \\| |/__ \\|  _|/  | |___   / ___ \\    /| |\\  U|  _  |u | |___    |  _ <    \r\n u |_|U  /_/   \\_\\   U/| |\\u   |_____| |_|     |_____| /_/   \\_\\  u |_|U   |_| |_|  |_____|   |_| \\_\\   \r\n _// \\\\_  \\\\    >>.-,_|___|_,-.//  \\\\  )(\\\\,-  <<   >>  \\\\    >>  _// \\\\_  //   \\\\  <<   >>   //   \\\\_  \r\n(__) (__)(__)  (__)\\_)-\' \'-(_/(_\")(\"_)(__)(_/ (__) (__)(__)  (__)(__) (__)(_\") (\"_)(__) (__) (__)  (__)");
                Console.ReadLine();
            }
        }
    }
}
