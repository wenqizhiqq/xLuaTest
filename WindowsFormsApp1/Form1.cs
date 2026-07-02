using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using XLua;

namespace WindowsFormsApp1
{
        // 必须加Hotfix，[CSharpCallLua]委托才能生效
        [Hotfix]
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }
        private void Form1_Load(object sender, EventArgs e)
        {
            TestByteChunk();
        }
        void TestByteChunk()
        {
            // using 自动释放LuaEnv，杜绝内存泄漏
              LuaEnv env = new LuaEnv();
            string luaCode = @"
local a = 10
local b = 20
return a + b, a - b
";
            byte[] luaBytes = System.Text.Encoding.UTF8.GetBytes(luaCode);
            object[] ret = env.DoString(luaBytes, "TestByteScript");

            // 安全转换：先转long，再转double，兼容整数/浮点数
            double sum = Convert.ToDouble(ret[0]);
            double diff = Convert.ToDouble(ret[1]);

            MessageBox.Show($"和={sum}, 差={diff}");
        }
    }
}
