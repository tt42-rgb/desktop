from desktop_organizer.app import main
from pathlib import Path
import sys

if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--self-test":
        from desktop_organizer.smoke import run
        raise SystemExit(run(Path(sys.argv[2])))
    main()
