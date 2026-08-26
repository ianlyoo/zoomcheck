# zoomcheck

Zoom attendance dashboard — match rosters with review queue and webhook-ready backend for large meeting automation.

[English](README.md)

[![CI](https://github.com/ianlyoo/zoomcheck/actions/workflows/ci.yml/badge.svg)](https://github.com/ianlyoo/zoomcheck/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Release: v0.1.0](https://img.shields.io/github/v/release/ianlyoo/zoomcheck?label=v0.1.0)](https://github.com/ianlyoo/zoomcheck/releases/tag/v0.1.0)
[![Pages](https://img.shields.io/badge/Pages-GitHub_Pages-2ea44f)](https://ianlyoo.github.io/zoomcheck/)

수업과 교육에서 이름, 별명, 기기명이 섞인 Zoom 출석을 처리합니다. Excel 로스터를 불러오고 Zoom 참여/퇴장 이벤트를 webhook으로 받아 로스터와 매칭합니다. 높은 신뢰도 매칭은 별도 확인 없이 처리하고 모호한 경우만 리뷰 큐로 보냅니다.

- .NET 8 + Avalonia 데스크톱, ASP.NET Core 백엔드, SQLite
- 설치 파일: `deploy/windows/` 아래 `ZoomCheck-Setup-x64.exe`
- 백엔드 `http://127.0.0.1:5078`에서 CSV 내보내기

## 빠른 시작 — dotnet와 webhook으로 roster-matching

dotnet 파이프라인으로 roster-matching과 webhook 기반 dashboard를 실행합니다.

### GitHub Release tarball로 설치

```bash
gh release download v0.1.0 --repo ianlyoo/zoomcheck --pattern "zoomcheck-*.tar.gz"
tar -xzf zoomcheck-0.1.0.tar.gz
```

`gh`가 없을 때 — 소스에서 빌드:

```bash
git clone https://github.com/ianlyoo/zoomcheck.git
cd zoomcheck
dotnet build ZoomCheck.sln
```

## 사용 사례 — attendance와 workflow-automation

대규모 수업에서 수동 확인이 어려운 출석 워크플로우에 적합합니다.

## 아키텍처: csharp Avalonia와 dotnet 백엔드

csharp, Avalonia, dotnet, attendance 흐름은 영문 README와 동일합니다.

## 벤치마크: 측정된 실행에서의 roster-matching

> 검증된 증거만 제시합니다. 출석 보장이 아닙니다.

**설정 (인접한 제한사항):** 120명 합성 클래스, 조건당 1회 실행, 100명 로스터, 픽스처 기반 Zoom 이벤트 재생, 로컬 SQLite, 측정 중 라이브 Zoom 연결 없음.

Limitations restated: 합성 픽스처, 1회 실행, 휴리스틱 임계값, 라이브 연결 없음, 로컬 데이터만, 출석 보증 없음.

## 프로젝트 링크

- Repository: https://github.com/ianlyoo/zoomcheck
- Pages: https://ianlyoo.github.io/zoomcheck/

## 라이선스

MIT — [LICENSE](LICENSE)를 참조하세요.
