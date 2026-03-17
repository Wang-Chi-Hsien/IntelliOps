# IntelliOps - AI-Powered Windows Log Sentinel

![License](https://img.shields.io/badge/license-MIT-blue.svg) ![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg) ![Status](https://img.shields.io/badge/status-MVP-green.svg)

**IntelliOps** is an intelligent Windows operations tool built on **WPF** and **Local LLM (Ollama)**.It monitors Windows Event Logs in real-time and utilizes RAG (Retrieval-Augmented Generation) technology alongside AI Agents to automatically analyze error root causes and provide repair suggestions.

##  Key Features

* ** Real-time Monitoring**: Captures Windows application errors in milliseconds via `EventLogWatcher`.
* ** AI Analysis**: Integrates **Qwen2.5-3b** (via Ollama) for deep interpretation of error logs and actionable advice.
* ** RAG Knowledge Base**: Features a built-in vector database that provides "fast-lane" instant solutions for known errors (e.g., 0x80040154), bypassing the need for heavy AI compute.
* ** Safe Automation**: Equipped with an AI Agent featuring safety guardrails to automatically execute some basic commands.

##  Tech Stack

* **Frontend**: C# / WPF / XAML
* **Core Framework**: .NET 8.0
* **AI Integration**: Microsoft Semantic Kernel
* **Local LLM**: Ollama (Model: Qwen2.5-3b)
* **Vector Embeddings**: Nomic-embed-text

##  Getting Started

### Prerequisites
Before using this software, please ensure Ollama is installed and the required models are downloaded:

1.  [Download and install Ollama](https://ollama.com/)
2. Open your terminal (CMD/PowerShell) and run the following commands:
    ```bash
    ollama pull Qwen2.5-3b
    ollama pull nomic-embed-text
    ```

### Installation & Run
1.  Download the [Latest Release] of this project.(https://github.com/your account/IntelliOps/releases)。
2.  Unzip the package and run `IntelliOps.WPF.exe`。
3.  The system will automatically detect and wake up the Ollama service.

## Screenshots
<img width="979" height="638" alt="Log處理器" src="https://github.com/user-attachments/assets/f4bdc3e9-b96a-4b27-8064-eb06deb6433f" />

##  License
This project is licensed under the MIT License.

---
 # IntelliOps -AI 智慧維運日誌管家

![License](https://img.shields.io/badge/license-MIT-blue.svg) ![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg) ![Status](https://img.shields.io/badge/status-MVP-green.svg)

**IntelliOps** 是一款基於 **WPF** 與 **Local LLM (Ollama)** 的智慧型 Windows 維運工具。它能即時監控 Windows Event Log，並利用 RAG (檢索增強生成) 技術與 AI 代理人，自動分析錯誤根因並提供修復建議。

##  主要功能 (Key Features)

* ** 即時監控 (Real-time Monitoring)**: 透過 `EventLogWatcher` 毫秒級捕捉 Windows 應用程式錯誤。
* ** AI 智慧分析 (AI Analysis)**: 整合 **Qwen2.5-3b** (via Ollama) 進行錯誤日誌的深度解讀與建議。
* ** RAG 知識庫 (Knowledge Base)**: 內建向量資料庫，針對已知錯誤 (如 0x80040154) 提供「快車道」秒級解法，無須消耗 AI 算力。
* ** 自動化修復 (Safe Automation)**: 具備安全護欄的 AI Agent，可自動執行基礎指令。

##  技術堆疊 (Tech Stack)

* **Frontend**: C# / WPF / XAML
* **Core Framework**: .NET 8.0
* **AI Integration**: Microsoft Semantic Kernel
* **Local LLM**: Ollama (Model: Qwen2.5-3b)
* **Vector Embeddings**: Nomic-embed-text

##  如何執行 (Getting Started)

### 前置需求 (Prerequisites)
在使用本軟體前，請確保您的電腦已安裝 Ollama 並下載模型：

1.  [下載並安裝 Ollama](https://ollama.com/)
2.  開啟終端機 (CMD) 執行以下指令下載模型：
    ```bash
    ollama pull Qwen2.5-3b
    ollama pull nomic-embed-text
    ```

### 安裝與執行
1.  下載本專案的 [最新 Release](https://github.com/你的帳號/IntelliOps/releases)。
2.  解壓縮並執行 `IntelliOps.WPF.exe`。
3.  系統將自動偵測並喚醒 Ollama 服務。

##  截圖
<img width="979" height="638" alt="Log處理器" src="https://github.com/user-attachments/assets/e592bb6c-7062-4c51-9bad-789c8d10849d" />

##  授權 (License)
本專案採用 MIT License。
