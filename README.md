XLua WinForm 桌面环境部署与运行说明
一、项目概述
本项目基于原生 XLua 源码，在 VS2022 Windows Forms 桌面框架下完成完整适配，剥离 Unity 引擎依赖，解决原生库编译、动态库加载、跨类型转换三类核心兼容问题，可支撑工控场景下 Lua 代码编辑器、断点单步调试、C# 与 Lua 双向互调等完整业务功能。

二、环境适配与问题解决
原生 C++ 动态库编译部署 通过 CMake 编译产出 64 位底层库，将编译产物xlua_x64.dll重命名为xlua.dll，配置文件自动复制至程序输出目录，同步将项目运行平台切换为 x64 架构，彻底解决运行时 “无法加载 xlua、找不到指定模块” 的 DLL 缺失异常。

XLua C# 源码去 Unity 剥离处理 提取 XLua 运行时核心Runtime与Gen源码，批量删除MonoType、UnityEngine、UnityEditor等 Unity 专属类型与预编译分支代码，消除标准.NET Framework 桌面环境不存在引擎类型导致的反射异常。

三、功能验证与代码优化
编写测试用例校验虚拟机运行能力：将 Lua 脚本以 UTF-8 编码转为二进制字节数组，调用DoString(byte[])重载接口执行代码；针对 Lua 整型默认映射 C# long、强制转换double报错问题，采用Convert.ToDouble()实现安全类型转换，成功读取脚本多返回值，弹窗输出运算结果，验证虚拟机加载、脚本执行链路完全通畅。

内存层面使用using语法包裹LuaEnv实例，程序退出作用域时自动释放 Lua 虚拟机资源，规避频繁创建虚拟机引发的内存泄漏问题。整套适配流程验证，剥离 Unity 后的原生 XLua 可稳定在 WinForm 桌面程序运行，满足工控 Lua 编辑器全套开发需求。 
![Uploading image.png…]()
