# HGLOS 고정 oracle

복사일: 2026-11-04. 원본은 수정하지 않았습니다.

- validator: `hglos-verify/scripts/validate_findings.py`
- schema: `hglos-verify/model-runner/findings-schema.json`
- 라이선스: `hglos/LICENSE` (동봉 MIT)
- schema SHA-256: `e1cb5ce06619bb501956e215bfb51304c74e99190b48e4b82a5474fced8caad5`
- validator SHA-256: `a2cbea5da71019e0e2feb263087d474d7f22cc4f26970188c616e6555abb4bb0`

테스트는 두 hash를 검사하고 `--schema`를 명시합니다. 테스트 Python은 `REVIEWGATE_TEST_PYTHON`으로 지정하며 없으면 실패합니다. 이 oracle은 HGLOS raw schema 호환성만 검사하며 실제 검수 품질·provenance 의미·독립성·회차 유효성을 승인하지 않습니다. 배포 CLI는 Python에 의존하지 않습니다.
