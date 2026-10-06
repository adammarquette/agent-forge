import sys

from security_platform.runner import main_run
from security_platform.serve import main_serve


def main(argv: list[str]) -> int:
    cmd = argv[1] if len(argv) > 1 else "serve"
    if cmd == "serve":
        return main_serve()
    if cmd == "run":
        return main_run()
    print("usage: python -m security_platform {serve|run}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
