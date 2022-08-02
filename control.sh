#!/bin/bash

BASEPATH="$(dirname "$PWD")"

TARGET=$1
CMD=$2

if [[ "$TARGET" == "api" ]]; then
    NAME=pawket-api
    WORKPATH=$BASEPATH/bin/api
    BINARY=WalletServer.dll
elif [[ "$TARGET" == "syncer" ]]; then
    NAME=pawket-syncer
    WORKPATH=$BASEPATH/bin/syncer
    BINARY=NodeDBSyncer.dll
else
    echo "wrong target type, must be api/syncer"
    exit 7
fi

PIDFILE=$BASEPATH/pid/${NAME}


if [[ "$CMD" == "status" ]]; then
    daemon --name=$NAME --pidfile=$PIDFILE --running
    if [ $? -ne 0 ]; then
        echo "$NAME is stopped"
    else
        echo "$NAME is running"
    fi
elif [[ "$CMD" == "start" ]]; then
    daemon --name=$NAME --pidfile=$PIDFILE --running
    if [ $? -ne 0 ]; then
		cd $WORKPATH
		source $BASEPATH/config/$NAME
		nohup daemon --name="$NAME" --respawn -f --pidfile=$PIDFILE --output=$BASEPATH/log/${NAME}.log dotnet $BINARY >/dev/null 2>&1 &
		echo "$NAME started"
    else
        echo "$NAME is already running"
    fi
elif [[ "$CMD" == "stop" ]]; then
    daemon --name=$NAME --pidfile=$PIDFILE --running
    if [ $? -ne 0 ]; then
        echo "$NAME is already stopped"
    else
		pid=$(head -n 1 $PIDFILE)
		daemon --name=$NAME --pidfile=$PIDFILE --stop
		echo -n "waiting previous process[$pid] exit"
		while [ -e /proc/$pid ]
		do
			echo -n .
			sleep .5
		done
		echo
		echo "$NAME stopped"
    fi
else
	echo "wrong command, supported: status/start/stop"
	exit 8
fi

