#!/usr/bin/env python3
"""Wine 下 PyInstaller 包装：绕过 init_sys_streams 问题，直接调用 API。"""
import sys
import os

# 确保 CWD 是项目根
os.chdir(os.path.dirname(os.path.abspath(__file__)))

from PyInstaller.__main__ import run

if __name__ == "__main__":
    run()